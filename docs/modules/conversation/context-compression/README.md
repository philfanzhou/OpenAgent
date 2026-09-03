
## Feature


上下文压缩由 Microsoft Agent Framework `CompactionProvider` 执行，不再维护平台压缩器。

会话压缩只有一种策略：MAF `SummarizationCompactionStrategy`。`ContextPolicy`
只配置摘要预算、模型上下文长度和保留消息组数，不再暴露策略选择字段。

压缩发生在 MAF 模型调用前，保持函数调用与函数结果的原子消息组，并可覆盖同一 Agent
run 内的后续工具迭代。

摘要策略之后追加 MAF `ContextWindowCompactionStrategy`，按最终上下文减去最大输出得到输入预算，收缩旧工具结果和历史消息组。该过程使用框架估算，并非供应商 tokenizer 的精确保证；配置优先级、输出参数支持能力和限制见 [模型 Token 限制](../../../integrations/llm-provider/TOKEN-LIMITS.md)。

## Architecture


```text
PlatformChatHistory -> MAF ChatMessage history
  -> FunctionInvokingChatClient
       -> CompactionProvider (before every model call)
            -> Audited SummarizationCompactionStrategy
            -> ContextWindowCompactionStrategy
       -> Provider IChatClient
```

`AgentFactory`（经 `ConversationHistoryFactory.CreateCompaction`）创建唯一的 MAF 摘要策略，并通过
`UseAIContextProviders` 将它放在 `FunctionInvokingChatClient` 内层、供应商模型客户端外层。因此用户发送后的第一次模型调用和
工具结果后的每次自主迭代都会重新检查阈值；最终回复完成后没有下一次模型调用，不会额外触发压缩。摘要调用复用已解析和授权的
`IChatClient`，不会通过第二套 Engine 请求或未授权的模型路径生成摘要。

平台会话存储保留完整审计历史；compaction 只决定本次模型调用看到的消息。

自动压缩通过审计包装记录触发方式、策略、原始消息范围、摘要或结果以及失败恢复状态。审计包装层不实现消息裁剪，
但会拒绝 token 节省不足 10% 的结果并恢复原始上下文，避免压缩后上下文反而膨胀。
`POST /api/v1/agent/conversations/{conversationId}/compact` 在校验租户、用户、会话归属和 Agent 授权后，使用同一 MAF
策略执行手动压缩。压缩失败时恢复调用前的消息组，完整会话消息不会被覆盖或删除。
最近一次成功的压缩结果（手动或自动）作为后续模型调用的历史视图，并自动拼接压缩后新增的原始消息；压缩发生在用户发送和 Agent 自主迭代的模型调用前，最终消息完成后不会额外触发压缩。

## Data Models


`ContextPolicy` 是 `AgentConfig` 的一部分，由 `IAgentRuntimeResolver` 从 Agent Profile
解析后提供给本次运行；客户端请求不再覆盖该策略：

| 字段 | 用途 |
|---|---|
| `ContextTokens`（LLM 配置） | 模型上下文硬上限；覆盖解析并预留最大输出后，自动摘要默认在有效输入预算的 80% 处触发，目标为其 50%；部署级固定触发阈值仍优先 |
| `PreserveRecentTurns` | 摘要压缩时保留的最近消息组数 |
| `SummarizeOptions` | 摘要模型调用的专用预算和模型配置；摘要 prompt 与普通对话上下文隔离 |

摘要策略直接使用 MAF 的 `SummarizationCompactionStrategy`，由框架选择并替换较旧的原子消息组，不再维护平台自定义摘要算法
或预处理 pipeline。摘要模型使用专用
压缩 prompt，`MaxSummaryTokens` 作为上限，同时受输入预算 20% 的比例预算约束；生成额度通过 `ChatOptions.MaxOutputTokens`
发送给支持该参数的模型，同模型摘要还受模型输出上限约束；返回后按 MAF 的 token 估算边界再次裁定。推理模型的生成上限会包含独立的 reasoning 余量，避免推理 token
耗尽上限后只留下空摘要。`PreserveRecentTurns` 直接映射为 MAF 的 `MinimumPreservedGroups`；这是硬性下限，因此最近消息组本身超过
目标预算时，框架不会继续压缩它们。生成的 summary assistant 消息写入当前会话的压缩投影，后续模型
调用使用“最近一次 summary + 之后新增消息”。

手动压缩是显式用户操作，不受自动阈值或 50% 目标限制，并允许 MAF 将全部非系统消息组压缩成一条摘要，因此只要会话中存在
可压缩消息就会调用摘要模型。模型生成结果未达到至少 10% token 节省时仍会被拒绝，且不会成为后续模型上下文；此类记录在
前端统一显示为“未执行”。

运行时状态、消息分组、trigger 和 target 均使用 MAF
`Microsoft.Agents.AI.Compaction` 类型，不复制平台 DTO。

每次实际自动压缩和每次手动尝试以 `ContextSummary` 追加到会话的数据库记录。Chat Inspector 展示压缩次数、最近策略、
触发方式、原始范围、摘要或结果；失败记录同时标识原始上下文是否已恢复。

单次请求可以降低或提高本次有效上下文窗口，但不得超过模型 Profile 声明的能力；其优先级高于 Agent 默认值。压缩使用最终有效值计算输入预算。
