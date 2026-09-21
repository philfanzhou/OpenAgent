# Conversation — 会话记录与存储

## 会话持久化

`IConversationStore` 是数据库无关契约。当前 `OpenAgent.Infrastructure` 使用 EF Core + PostgreSQL 作为持久化实现，不支持其他数据库 Provider。

生产链路使用写穿透组合：PostgreSQL 先完成会话、消息与文件引用的事务性写入，成功后把完整会话写入 Redis 热副本。读取优先命中 Redis，未命中时从 PostgreSQL 回填。Redis 从不作为事实源，缓存失败不会回滚已提交的数据库写入。

每次成功 assistant 响应把 Provider 终态 `TokenUsage` 与 `ModelId` 保存到对应消息；取消、失败或 Provider 未返回完整 usage 时保持 `TokenUsage=null`。会话累计值由持久化消息按 `MessageId` 派生，不保存可漂移的独立计数器，因此重载历史、切换会话和缓存回填不会重复累计。只要任一响应缺 usage，完整会话累计即不可精确计算。

`IConversationLock` 是独立于存储的协调契约（见 [execution.md](./execution.md) 的会话锁）：Redis 已配置时使用带租约心跳的分布式锁，保证同一 `{tenantId}:{conversationId}` 在多个 Engine 实例间串行执行；未配置 Redis 的单实例开发环境使用进程内锁。两种锁都不承担数据存储职责。

## 交互日志与轮次追溯

所有发往大模型的交互（Agent 轮次内的每次调用，含工具循环迭代；自动/手动压缩摘要调用）由 `LlmInteractionRecorder`（挂在 `AgentChatClientFactory` 产出的 provider client 最外层）捕获，写入 `openagent.llm_interaction_logs`（见 [database.md](../database.md)），同时输出一条摘要结构化日志（EventId 1460/1461）。载荷经过脱敏投影：API Key 永不落日志，二进制内容仅记录占位符，超长字段按 `LlmInteraction:MaxContentLength` 截断。记录失败只降级为警告，绝不影响对话主流程；`LlmInteraction:Enabled=false` 可整体关闭。

轮次关联键是每次请求携带的 `X-Trace-Id`：同一轮内所有 LLM 调用与该轮全部落库消息（`conversation_messages.TraceId`，由 `ConversationSessionStore.SaveAsync` 统一盖章）共享同一个值，据此可把消息时间线与交互日志按轮次对齐做原始会话状态分析。

查询 API：`GET /api/v1/agent/conversations/{id}/llm-interactions`（租户 + 属主校验，按 `(StartedAt, CallIndex)` 升序分页）。Router 已注册同路径转发。

## 上下文压缩

上下文压缩由 Microsoft Agent Framework `CompactionProvider` 执行，平台不再维护自有压缩器。会话压缩只有一种策略：MAF `SummarizationCompactionStrategy`。`ContextPolicy` 只配置摘要预算、模型上下文长度和保留消息组数，不暴露策略选择字段。压缩发生在 MAF 模型调用前，保持函数调用与函数结果的原子消息组，并可覆盖同一 Agent run 内的后续工具迭代。

```text
PlatformChatHistory -> MAF ChatMessage history
  -> FunctionInvokingChatClient
       -> CompactionProvider (before every model call)
            -> SummarizationCompactionStrategy
       -> Provider IChatClient
```

`AgentFactory`（经 `ConversationHistoryFactory.CreateCompaction`）创建唯一的 MAF 摘要策略，并通过 `UseAIContextProviders` 将它放在 `FunctionInvokingChatClient` 内层、供应商模型客户端外层。因此用户发送后的第一次模型调用和工具结果后的每次自主迭代都会重新检查阈值；最终回复完成后没有下一次模型调用，不会额外触发压缩。摘要调用复用已解析和授权的 `IChatClient`，不会通过第二套 Engine 请求或未授权的模型路径生成摘要。

平台会话存储保留完整审计历史；compaction 只决定本次模型调用看到的消息。自动压缩通过审计包装记录触发方式、策略、原始消息范围、摘要或结果以及失败恢复状态；审计包装层不实现消息裁剪，但会拒绝 token 节省不足 10% 的结果并恢复原始上下文，避免压缩后上下文反而膨胀。

`ContextPolicy` 是 `AgentConfig` 的一部分，由 `IAgentRuntimeResolver` 从 Agent Profile 解析后提供给本次运行；客户端请求不覆盖该策略：

| 字段 | 用途 |
|---|---|
| `ContextTokens`（LLM 配置） | 模型上下文 token 上限；自动压缩在其 80% 处触发，压缩目标为其 50%，未配置时临时使用 1000 token |
| `PreserveRecentTurns` | 摘要压缩时保留的最近消息组数 |
| `SummarizeOptions` | 摘要模型调用的专用预算和模型配置；摘要 prompt 与普通对话上下文隔离 |

摘要策略直接使用 MAF 的 `SummarizationCompactionStrategy`，由框架选择并替换较旧的原子消息组。摘要模型使用专用压缩 prompt，`MaxSummaryTokens` 作为上限，同时受上下文 20% 的比例预算约束；该限制通过 `ChatOptions.MaxOutputTokens` 实际传给模型，并在返回后按 MAF 的 token 估算边界再次裁定。推理模型的生成上限包含独立的 reasoning 余量，避免推理 token 耗尽上限后只留下空摘要。`PreserveRecentTurns` 直接映射为 MAF 的 `MinimumPreservedGroups`（硬性下限，最近消息组本身超过目标预算时框架不会继续压缩它们）。生成的 summary assistant 消息写入当前会话的压缩投影，后续模型调用使用“最近一次 summary + 之后新增消息”。

手动压缩：`POST /api/v1/agent/conversations/{conversationId}/compact` 在校验租户、用户、会话归属和 Agent 授权后，使用同一 MAF 策略执行。手动压缩是显式用户操作，不受自动阈值或 50% 目标限制，并允许 MAF 将全部非系统消息组压缩成一条摘要，因此只要会话中存在可压缩消息就会调用摘要模型。模型生成结果未达到至少 10% token 节省时仍会被拒绝，且不会成为后续模型上下文；此类记录在前端统一显示为“未执行”。压缩失败时恢复调用前的消息组，完整会话消息不会被覆盖或删除。

运行时状态、消息分组、trigger 和 target 均使用 MAF `Microsoft.Agents.AI.Compaction` 类型，不复制平台 DTO。每次实际自动压缩和每次手动尝试以 `ContextSummary` 追加到会话的数据库记录；Chat Inspector 展示压缩次数、最近策略、触发方式、原始范围、摘要或结果，失败记录同时标识原始上下文是否已恢复。
