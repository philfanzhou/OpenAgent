# Conversation — 会话记录与存储

本域覆盖会话持久化、LLM 交互日志、上下文压缩与会话锁。`IConversationStore` 是数据库无关契约，当前 `OpenAgent.Framework` 使用 EF Core + PostgreSQL 作为持久化实现，不支持其他数据库 Provider。

## 会话持久化

生产链路使用写穿透组合：PostgreSQL 先完成会话、消息与文件引用的事务性写入，成功后把完整会话写入 Redis 热副本。读取优先命中 Redis，未命中时从 PostgreSQL 回填。Redis 从不作为事实源，缓存失败不会回滚已提交的数据库写入。

每次成功 assistant 响应把 Provider 终态 `TokenUsage` 与 `ModelId` 保存到对应消息；取消、失败或 Provider 未返回完整 usage 时保持 `TokenUsage=null`。会话累计值由持久化消息按 `MessageId` 派生，不保存可漂移的独立计数器，因此重载历史、切换会话和缓存回填不会重复累计。只要任一响应缺 usage，完整会话累计即不可精确计算。

### 会话访问边界

会话锁只负责同一租户、同一会话的并发协调，不授予访问权限。数据库、内存存储与 Redis 热副本读取均使用请求作用域的 `ICurrentUserContext`：必须已认证且用户、租户非空；请求租户、记录租户、所有者与会话 ID 精确匹配，不忽略大小写。创建会话也必须匹配当前身份，不能通过指定 `UserId` 或 `TenantId` 替他人创建记录。数据库与内存存储拒绝对软删除会话的读写，缓存读取拒绝已标记删除的载荷；缓存失效失败时仍可能读到属于原所有者的旧副本，不提供删除即时不可读的保证。

`ConversationSessionStore` 在加载、保存及版本冲突重试时校验租户、用户、会话 ID、会话类型和删除状态；用户侧详情、删除、交互日志与手动压缩入口仅接受未删除的 `User` 会话。`Running`、`Completed`、`Failed`、`Cancelled` 状态不改变上述授权规则。

## 交互日志与轮次追溯

所有发往大模型的交互（Agent 轮次内的每次调用，含工具循环迭代；自动/手动压缩摘要调用）由 `LlmInteractionRecorder`（紧贴 `AgentChatClientFactory` 产出的 provider client，出站消息规格化层在其外层）捕获，记录的是规格化（空 assistant 文本剥离、空工具结果占位、null 工具参数规格化为 `{}`）后真正发往 provider 的最终消息，写入 `openagent.llm_interaction_logs`（见 [LlmInteractionLogs](../database/tables/LlmInteractionLogs.md)），同时输出一条摘要结构化日志（EventId 1460/1461）。载荷经过脱敏投影：API Key 永不落日志，二进制内容仅记录占位符，超长字段按 `LlmInteraction:MaxContentLength` 截断。记录失败只降级为警告，绝不影响对话主流程；`LlmInteraction:Enabled=false` 可整体关闭。

轮次关联键是每次请求携带的 `X-Trace-Id`：同一轮内所有 LLM 调用与该轮全部落库消息（`conversation_messages.TraceId`，由 `ConversationSessionStore.SaveAsync` 统一盖章）共享同一个值，据此可把消息时间线与交互日志按轮次对齐做原始会话状态分析。

查询 API：`GET /api/v1/agent/conversations/{id}/llm-interactions`（租户 + 属主校验，按 `(StartedAt, CallIndex)` 升序分页）。Router 已注册同路径转发。

## 上下文压缩

上下文压缩由平台自持实现（`OpenAgent.Application/Conversation/Compaction/`），不依赖外部 Agent 框架的 CompactionProvider。会话压缩只有一种策略：`SummarizationCompactionStrategy`；`ContextPolicy` 只配置摘要预算、模型上下文长度和保留消息组数，不暴露策略选择字段。

```text
PlatformChatHistory -> 平台 ChatMessage history
  -> RuntimePreparation
       -> CompactionContextProvider（每次模型调用前）
            -> CompactionProvider.CompactAsync
                 -> AuditedCompactionStrategy -> SummarizationCompactionStrategy
       -> RuntimeInvocation.Messages -> 运行时驱动
```

触发时机：`RuntimePreparation`（经 `ConversationHistoryFactory.CreateCompaction`）创建唯一的摘要策略，并把它注册为 `IAgentContextProvider` 挂在本轮执行上下文上。用户发送后的第一次模型调用和工具结果后的每次自主迭代都会重新检查阈值；保持函数调用与函数结果的原子消息组，可覆盖同一 Agent run 内的后续工具迭代。最终回复完成后没有下一次模型调用，不会额外触发压缩。摘要调用复用已解析和授权的 `IChatClient`，不会通过第二套 Engine 请求或未授权的模型路径生成摘要。

平台会话存储保留完整审计历史；compaction 只决定本次模型调用看到的消息。最近一次成功的压缩结果（手动或自动）作为后续模型调用的历史视图，并自动拼接压缩后新增的原始消息。

审计与恢复：自动压缩通过审计包装记录触发方式、策略、原始消息范围、摘要或结果以及失败恢复状态。审计层不实现消息裁剪，但会拒绝 token 节省不足 10% 的结果并恢复原始上下文，避免压缩后上下文反而膨胀；压缩失败时恢复调用前的消息组，完整会话消息不会被覆盖或删除。每次实际自动压缩和每次手动尝试以 `ContextSummary` 追加到会话的数据库记录，Chat Inspector 展示压缩次数、最近策略、触发方式、原始范围、摘要或结果，失败记录同时标识原始上下文是否已恢复。

策略参数（`ContextPolicy` 是 `AgentConfig` 的一部分，由 `IAgentRuntimeResolver` 从 Agent Profile 解析后提供给本次运行；客户端请求不再覆盖该策略）：

| 字段 | 用途 |
|---|---|
| `ContextTokens`（LLM 配置） | 模型上下文 token 上限；自动压缩在其 80% 处触发，压缩目标为其 50%，未配置时临时使用 1000 token |
| `PreserveRecentTurns` | 摘要压缩时保留的最近消息组数（硬性下限 `MinimumPreservedGroups`，最近消息组超过目标预算时不会继续压缩它们）|
| `SummarizeOptions` | 摘要模型调用的专用预算和模型配置；摘要 prompt 与普通对话上下文隔离 |

摘要实现：保留最近 `MinimumPreservedGroups` 个轮次，其余轮次交给摘要模型，替换为一个带 summary 标记的消息组。摘要模型使用专用压缩 prompt，`MaxSummaryTokens` 作为上限，同时受上下文 20% 的比例预算约束；该限制经 `ChatOptions.MaxOutputTokens` 实际传给模型，并在返回后按 token 估算边界再次裁定。推理模型的生成上限包含独立的 reasoning 余量，避免推理 token 耗尽上限后只留下空摘要。生成的 summary assistant 消息写入当前会话的压缩投影，后续模型调用使用"最近一次 summary + 之后新增消息"。

手动压缩：`POST /api/v1/agent/conversations/{conversationId}/compact` 在校验租户、用户、会话归属和 Agent 授权后，使用同一策略执行。手动压缩是显式用户操作，不受自动阈值或 50% 目标限制，允许将全部非系统消息组压缩成一条摘要，只要会话中存在可压缩消息就会调用摘要模型。模型生成结果未达到至少 10% token 节省时仍会被拒绝，且不会成为后续模型上下文；此类记录在前端统一显示为"未执行"。

## 会话锁

`IConversationLock` 是独立于存储的协调契约，按 `{tenantId}:{conversationId}` 串行化请求。配置 Redis 时，`RedisConversationLock` 使用 `SET NX` 获取带 TTL 的分布式锁，并在长推理期间以 Lua 校验所有者续租；锁获取失败时请求返回冲突，释放由 `IConversationLockHandle.DisposeAsync` 完成，保证同一会话在多个 Engine 实例间串行执行。未配置 Redis 时，`InMemoryConversationLock` 仅用于单个 Engine 进程的开发/测试降级。两种锁都不承担数据存储职责。

跨实例写入正确性同时由分布式锁和数据库乐观并发保障：当前 EF Core Provider 以 `expectedVersion` 与并发令牌执行检查，冲突时不覆盖已提交的会话消息。`IConversationLock` 不绑定 Redis；后续可以替换为数据库 advisory lock、Consul 等协调 Provider，而不改变会话存储或业务契约。Redis 仅保存锁令牌和可失效热副本，不是会话或文件资产的持久化存储。

`PlatformChatHistory` 只有在加锁、所有权校验和历史加载全部成功后才开放持久化。正常完成、失败、取消和资源释放均要求本请求仍持有锁；锁冲突、加载或授权失败只清理资源，不追加用户消息、不修改会话状态。`ConversationSessionStore` 接受本轮锁句柄，在每次追加（含冲突重试）和状态更新发起前复核租户、会话 ID 与 `IsHeld`，覆盖异步读取或前一次写入等待期间丢锁的情况；已发起的数据库操作仍由事务和乐观并发约束，不将本地持锁检查视为数据库 fencing。Redis 续租被拒绝、续租异常或本地租期到期后，`IsHeld` 返回 `false`，不会因句柄尚未释放而继续报告持锁。

## 验证

回归测试覆盖跨用户、跨租户、大小写不同的身份、缺失身份、缓存命中／异常回退、软删除、锁冲突，以及在加载、保存读取、冲突重读和追加等待期间丢锁；并验证合法用户的正常完成、失败部分输出和取消持久化。失败绕过回归在修复前实测 6 项全部失败，拒绝请求仍使会话版本从 7 增至 9 或状态变为 `Failed`。

本地自动检查：`dotnet build Backend/OpenAgent.sln --no-restore` 通过；下列筛选测试 224 项通过、5 项因真实 OpenCode 进程门控跳过。该自动检查中的 EF 存储测试使用 InMemory Provider，Redis 使用测试替身；真实部署验收另见下文。

```sh
dotnet test Backend/OpenAgent.sln --no-build --no-restore \
  --filter 'FullyQualifiedName~Conversation|FullyQualifiedName~AcpExecutionTests|FullyQualifiedName~AgentExecutorUsageTests|FullyQualifiedName~AgentUserContextMiddlewareTests|FullyQualifiedName~LlmInteractionEndpointTests|FullyQualifiedName~ApplicationModuleDependencyTests'
```

### 远程部署验收（2026-11-12）

实际目标为 `alg@192.168.2.159`，沿用既有 Compose 项目 `openagent-mcp-preview-db3d84c` 与访问地址 `https://192.168.2.159:18081`。仅将 Engine 更新为 `openagent-mcp-preview-engine:session-owner-d4ce6a9292fe`；镜像来自 `8e595b3b` 基线加本次未提交的工作区修复，源码归档 SHA-256 为 `d4ce6a9292fe078afbf1b0f02e728371fa26f49be0e3ffec18077cbdb32ce5e4`，内嵌 OpenCode 1.19.3。

部署前备份现有 PostgreSQL 数据库与 Data Protection 密钥卷。原认证、连接配置、Chat、MCP 服务及持久卷均未改动；原有 7 个会话及消息的完整指纹在更新和验收后保持一致。Docker Engine 容器为 `healthy`，HTTPS 页面与 readiness 均返回 200；readiness 的 `Degraded` 与更新前一致（现有环境未部署 Redis，部分模型连接是故意返回错误的验证配置），不表示所有外部依赖均已健康。

- 通过真实 HTTPS、远程 PostgreSQL 与镜像内 OpenCode 验证：正常执行为 `Completed`，模型认证错误为 `Failed`，收到 `runtime` 事件、确认执行初始化后断开 SSE 为 `Cancelled`。仅收到初始 `conversation` 元事件就断开时，不保证创建会话，不能用来判定已初始化轮次的取消状态。
- 使用原有认证账号，以合成会话构造不同所有者、不同租户及仅大小写不同的所有者；四种状态共 12 组、84 次路由检查。详情、交互日志、删除、压缩、普通聊天、intent 和流式聊天均拒绝越权；数据库会话和消息快照不变，模型验证服务计数不增加。未新增账号或修改认证方式。
- 同一所有者的实际运行中请求拒绝并发调用（409），原持锁请求正常完成；所有者可继续失败和取消的会话，旧历史保留。
- 未认证查询返回 401，伪造租户 Header 返回 403，列表／搜索不包含合成的非所属记录。所有本次创建的测试会话均已清理，验收后仍只有原来的 7 个会话。

验收复用远程既有的内部确定性模型服务，不调用外部模型账户；不代表真实 Redis 分布式锁、Router 或附件验收。部署状态、回退所需的原 Compose 文件清单、数据库与密钥备份以及无凭据验收报告保存在远程 `~/openagent-session-owner-deploy/`（`deployment-before.json`、`deployment-after.json`、`verification.json`）；旧镜像 `openagent-mcp-preview-engine:f33b51e` 保留用于回退。回退后需重载现有 Chat 容器的 Nginx，使其重新解析 Engine 地址；不得删除原有数据卷。

此前误建的本机预览、独立 PostgreSQL 数据、测试工具和 `.preview/` 产物已按用户要求卸载；本机 5184、5240、55432 端口及相关进程均已清理，工作区代码改动保留。

## 代码入口

- 存储契约与实现：`Backend/src/OpenAgent.Contracts/`（IConversationStore、IConversationLock）、`Backend/src/OpenAgent.Framework/`（EF Core + PostgreSQL 实现）。
- 压缩：`Backend/src/OpenAgent.Application/Conversation/Compaction/`。
- 交互记录：`Backend/src/OpenAgent.Application/ModelProviders/`（LlmInteractionRecorder，紧贴 AgentChatClientFactory）。
- 测试：`Backend/tests/OpenAgent.Application.Tests/Conversation/`（History/、Lock/、Compaction/）。
