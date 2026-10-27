# Conversation — 会话记录与存储

本域覆盖会话持久化、LLM 交互日志、上下文压缩与会话锁。`IConversationStore` 是数据库无关契约，当前 `OpenAgent.Framework` 使用 EF Core + PostgreSQL 作为持久化实现，不支持其他数据库 Provider。

## 会话持久化

生产链路使用写穿透组合：PostgreSQL 先完成会话、消息与文件引用的事务性写入，成功后把完整会话写入 Redis 热副本。读取优先命中 Redis，未命中时从 PostgreSQL 回填。Redis 从不作为事实源，缓存失败不会回滚已提交的数据库写入。

每次成功 assistant 响应把 Provider 终态 `TokenUsage` 与 `ModelId` 保存到对应消息；取消、失败或 Provider 未返回完整 usage 时保持 `TokenUsage=null`。会话累计值由持久化消息按 `MessageId` 派生，不保存可漂移的独立计数器，因此重载历史、切换会话和缓存回填不会重复累计。只要任一响应缺 usage，完整会话累计即不可精确计算。

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

## 代码入口

- 存储契约与实现：`Backend/src/OpenAgent.Contracts/`（IConversationStore、IConversationLock）、`Backend/src/OpenAgent.Framework/`（EF Core + PostgreSQL 实现）。
- 压缩：`Backend/src/OpenAgent.Application/Conversation/Compaction/`。
- 交互记录：`Backend/src/OpenAgent.Application/ModelProviders/`（LlmInteractionRecorder，紧贴 AgentChatClientFactory）。
- 测试：`Backend/tests/OpenAgent.Application.Tests/Conversation/`（History/、Lock/、Compaction/）。
