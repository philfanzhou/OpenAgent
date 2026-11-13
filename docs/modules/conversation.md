# Conversation — 会话记录与存储

本域覆盖会话持久化、LLM 交互日志、上下文压缩与会话锁。`IConversationStore` 是数据库无关契约，当前 `OpenAgent.Framework` 使用 EF Core + PostgreSQL 作为持久化实现，不支持其他数据库 Provider。

## 会话持久化

生产链路使用写穿透组合：PostgreSQL 先完成会话、消息与文件引用的事务性写入，成功后把完整会话写入 Redis 热副本。读取优先命中 Redis，未命中时从 PostgreSQL 回填。Redis 从不作为事实源，缓存失败不会回滚已提交的数据库写入。

每次成功 assistant 响应把 Provider 终态 `TokenUsage` 与 `ModelId` 保存到对应消息；取消、失败或 Provider 未返回完整 usage 时保持 `TokenUsage=null`。会话累计值由持久化消息按 `MessageId` 派生，不保存可漂移的独立计数器，因此重载历史、切换会话和缓存回填不会重复累计。只要任一响应缺 usage，完整会话累计即不可精确计算。

### 会话访问边界

会话锁只负责同一租户、同一会话的并发协调，不授予访问权限。数据库、内存存储与 Redis 热副本读取均使用请求作用域的 `ICurrentUserContext`：必须已认证且用户、租户非空；请求租户、记录租户、所有者与会话 ID 精确匹配，不忽略大小写。创建会话也必须匹配当前身份，不能通过指定 `UserId` 或 `TenantId` 替他人创建记录。数据库与内存存储拒绝对软删除会话的读写，缓存读取拒绝已标记删除的载荷；缓存失效失败时仍可能读到属于原所有者的旧副本，不提供删除即时不可读的保证。

`ConversationSessionStore` 在加载、保存及版本冲突重试时校验租户、用户、会话 ID、会话类型和删除状态；用户侧详情、删除与交互日志入口仅接受未删除的 `User` 会话。`Running`、`Completed`、`Failed`、`Cancelled` 状态不改变上述授权规则。

## 交互日志与轮次追溯

模型推理、工具循环与自动压缩均由 ACP 对端 OpenCode 执行。平台不创建 `IChatClient`，不通过 OpenAI/Anthropic SDK 直接调用模型。原 `LlmInteractionRecorder` 与摘要模型工厂已删除；交互日志表及查询接口继续支持读取历史记录，不能据此认为当前 ACP 执行会保存每次模型调用的原始请求和响应。

轮次请求使用 `X-Trace-Id` 关联平台消息与执行日志。ACP 对端的模型错误经 JSON-RPC 返回，由 `OpenCodeErrorClassifier` 分类；旧平台 `.NET OpenAIChatClient` 堆栈不属于当前 OpenCode 自动压缩路径。

查询 API：`GET /api/v1/agent/conversations/{id}/llm-interactions`（租户 + 属主校验，按 `(StartedAt, CallIndex)` 升序分页）。Router 已注册同路径转发。

## 上下文压缩

自动压缩由 OpenCode 原生负责。`RuntimePreparation` 只准备平台历史、附件与工具，随后通过 `RuntimeAgentExecutor` → `OpenCodeRuntime` → ACP `session/prompt` 执行，不在请求前或工具循环中另发平台摘要请求。

平台已删除自动/手动摘要策略、模型客户端工厂、DI 注册，以及 Engine/Router 的 `POST /api/v1/agent/conversations/{conversationId}/compact` 路由；前端不再提供“手动压缩”按钮。旧 `ContextPolicy` 配置不再控制运行时压缩阈值、摘要预算或摘要模型。

ACP `compaction_update` 和 `compaction_summary_chunk` 经 `AcpCompactionMapper`、`AcpEventMapper` 转为平台 `compaction` 事件，SSE 实时更新前端时间线，并在轮次完成、失败或取消的持锁保存路径中写入 `ContextSummary`。同一原生 session + compaction ID 对应稳定的平台 ID；分块追加、整体替换、显式 null/空数组清除均按协议处理。审计写入按 ID 原位更新，重放不新增重复记录；未收到终态的进行中记录在轮次结束时标为中断/取消。

仓库锁定的 OpenCode 1.19.3 实测不发送上述标准事件，且会把摘要作为普通 assistant 文本输出。兼容适配在 prompt 更新队列读完后调用同一可执行文件的 `export <sessionId>`，读取原生 `assistant.summary=true` 元数据，再补发压缩事件；不按回复文案或 token 下降猜测压缩，不新增模型调用。已报告的原生摘要 ID 随检查点保存，冷启动不重复发出旧摘要。导出输出只在内存中解析（上限 32 MiB），stderr 丢弃，不保存完整导出文件；失败只输出脱敏告警。该版本的摘要审计在轮次结束后可见，实时正文仍遵循其原有 ACP 输出行为。

原生压缩审计的 `IsRuntimeEvent=true`，携带 `RuntimeSessionId`、`RuntimeCompactionId` 和轮次 `TraceId`；`SourceEndSequence` 仅是平台接收到事件时的时间线锚点，不代表原生上下文的覆盖边界。`CompactedMessages` 保持空，窗口/计费用量未知时前端不显示零值。可展示摘要不替代模型上下文；已有完整会话消息及旧有效压缩投影仍可读取。

原生会话状态通过 `OpenCode:SessionStateRoot` 保存（镜像为 `/home/app/openagent-sessions`，Compose 挂载 `engine-sessions`）。平台消息与审计提交成功后，才以临时文件 + 原子替换写入原生 session ID、归属/配置身份、工具 schema、历史检查点及已报告压缩 ID；下轮开始前使旧检查点失效，避免崩溃/失败轮次恢复到未提交状态。目录为服务账户私有，文件租约防止两个进程同时修改同一原生状态。进程空闲回收、池淘汰与 Engine 正常关闭只结束进程，保留已提交的原生数据库和工作区。

下次请求优先恢复同一身份且检查点匹配的原生 session，只发送本轮增量；原生会话保留其真实压缩上下文。状态丢失、配置/工具 schema/历史改变、对端不支持 resume/close 或 resume 被拒绝时，创建新 session 并回放平台历史，日志记录选择或降级原因。不把展示摘要拼成不完整的替代历史。默认状态为本机文件，不提供跨实例数据库复制；多实例需要会话黏性或正确挂载受控共享存储，缺少原生状态的实例将降级回放。

排查旧 `.NET OpenAIChatClient` 压缩异常时，需核对实际部署版本、日志时间和运行实例。Git 提交 `cd509de8` 移除了 `RuntimePreparation` 中的平台自动摘要调用；此前即使普通聊天通过 ACP，自动压缩仍可能通过平台 SDK 直调模型。[推断] 自动压缩期间出现该 SDK 堆栈，可能来自仍包含旧链路的部署或历史记录；仅凭最新源码无法确定正式环境的具体情况。

## 会话锁

`IConversationLock` 是独立于存储的协调契约，按 `{tenantId}:{conversationId}` 串行化请求。配置 Redis 时，`RedisConversationLock` 使用 `SET NX` 获取带 TTL 的分布式锁，并在长推理期间以 Lua 校验所有者续租；锁获取失败时请求返回冲突，释放由 `IConversationLockHandle.DisposeAsync` 完成，保证同一会话在多个 Engine 实例间串行执行。未配置 Redis 时，`InMemoryConversationLock` 仅用于单个 Engine 进程的开发/测试降级。两种锁都不承担数据存储职责。

跨实例写入正确性同时由分布式锁和数据库乐观并发保障：当前 EF Core Provider 以 `expectedVersion` 与并发令牌执行检查，冲突时不覆盖已提交的会话消息。`IConversationLock` 不绑定 Redis；后续可以替换为数据库 advisory lock、Consul 等协调 Provider，而不改变会话存储或业务契约。Redis 仅保存锁令牌和可失效热副本，不是会话或文件资产的持久化存储。

`PlatformChatHistory` 只有在加锁、所有权校验和历史加载全部成功后才开放持久化。正常完成、失败、取消和资源释放均要求本请求仍持有锁；锁冲突、加载或授权失败只清理资源，不追加用户消息、不修改会话状态。`ConversationSessionStore` 接受本轮锁句柄，在每次追加（含冲突重试）和状态更新发起前复核租户、会话 ID 与 `IsHeld`，覆盖异步读取或前一次写入等待期间丢锁的情况；已发起的数据库操作仍由事务和乐观并发约束，不将本地持锁检查视为数据库 fencing。Redis 续租被拒绝、续租异常或本地租期到期后，`IsHeld` 返回 `false`，不会因句柄尚未释放而继续报告持锁。

## 验证

2026-11-13 清理与持久恢复验证：后端解决方案构建通过，默认套件 1166 项通过、37 项环境门控跳过；真实 OpenCode 1.19.3 + 确定性本地模型运行时套件 125 项通过、3 项平台门控跳过；前端 `pnpm check` 通过（185 项测试及类型/生产构建）。新增覆盖标准 ACP 补丁语义、压缩审计落库/失败路径/EF 重放去重、SSE/前端原位更新、真实自动压缩后跨进程恢复、状态缺失或恢复被拒绝后的平台历史回放，以及取消/提交失败时原生状态作废。架构门禁禁止 Application 引用模型 SDK 或 `IChatClient`；旧压缩入口返回 404。未部署正式环境；未验证真实外部模型、跨实例共享文件系统、Windows 持久卷 ACL 或 Docker 镜像构建。


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
- 自动压缩执行：`Backend/src/OpenAgent.Runtime/OpenCode/OpenCodeRuntime.cs`（ACP 对端 OpenCode）。
- 历史交互查询：`Backend/src/OpenAgent.Engine.Host/Endpoints/Conversations/ConversationEndpoints.cs`。
- 测试：`Backend/tests/OpenAgent.Application.Tests/Conversation/`（History/、Lock/）。
