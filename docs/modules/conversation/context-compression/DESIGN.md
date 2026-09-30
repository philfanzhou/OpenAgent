# 上下文压缩实现

## 触发链路

`ConversationStore:EnableAutoCompaction` 默认 `true`。`AgentFactory` 在函数调用循环内层安装 `CompactionBudgetChatClient → MAF CompactionProvider → provider`，同步、流式以及每次工具循环续接都在请求前检查。

自动阈值为 `min(窗口 × 80%, 窗口 - 输出预留)`。`CompactionOutputReserveTokens` 默认 4096，按模型窗口 20% 封顶；消息由 MAF 估算，指令、工具名称、描述和 schema 由预算客户端补计。这是 UTF-8 字节数 / 4 的近似值，并非 provider tokenizer 的精确计数；缓存仍占上下文，累计计费量不参与触发。

手动入口为 `POST /api/v1/agent/conversations/{id}/compact?llmProfileId=...`，校验租户、用户和会话归属后获取与执行相同的会话锁。手动不等待自动阈值；没有旧内容或收益不足时返回 `Skipped`，不会把全部 user 消息换成 assistant 摘要。

## 保留与摘要

`TurnCompactionStrategy` 使用 MAF 原子组，保留系统消息。`PreserveRecentTurns` 按用户轮次计算，默认 2，最小 1；自动目标默认窗口 50%，预算不足时可以进一步摘要较早轮次。当前用户请求始终保留；长工具循环可摘要旧工具组，保留最新完整调用/结果组。展开存储的并行调用先由 `PlatformChatHistory.RepairToolHistory` 合并后再分组。

摘要使用专用 prompt 与 user 角色的 JSON transcript；工具历史是引用数据，不作为待执行的调用。只含旧摘要或工具结果的候选也满足严格网关的 user 消息要求。`MaxSummaryTokens` 是上限，同时不超过窗口 20% 的比例预算；生成额度包含 reasoning 余量并受窗口约束。超长 transcript 分段提交，滚动合并上一段状态，全部段成功后才改变索引。

审计包装拒绝空摘要、不可用摘要和 token 节省不足 10% 的结果。失败、取消或审计写入失败会恢复原消息组，取消向调用方传播。同一次自动运行内，相同原始边界不重复尝试；手动仍可重试。

## 持久化

原始 `conversation_messages` 不删除、不覆盖。`ContextSummary` 追加审计，最近成功的 `CompactedMessages` 加上 `Sequence > SourceEndSequence` 的新原始消息构成模型历史。

`CompactionMessageMetadata` 在 SDK 消息上保存原始序号。历史加载、并行工具块合并、当前 user 和工具结果展开均保留边界，摘要继承覆盖的末序号。轮次结束前的自动投影因此精确覆盖已经产生的消息，不依赖合并后的消息数量，也不按相同文本猜测重复。摘要标记和附件 fileId 在投影往返时保留。新记录使用 `ProjectionVersion=1`；旧记录默认 0，仅旧投影保留文本重叠兼容修复，新记录按原始序号续接，不吞掉重复用户文本。

失败或跳过不会替换前一次成功投影；成功投影写入存储后，本次模型上下文才提交压缩结果。

## 前端

`useConversationState` 按会话 ID 跟踪压缩状态、拦截重复点击，按 compressionId 去重并刷新服务端详情。切换会话不会串写结果；详情刷新失败仍保留返回的审计。

`useChatStreaming.send()` 拦截压缩中发送并保留草稿。输入区和 Inspector 均提供手动入口，窄窗口仍可使用。执行结束、取消或错误后加载服务端审计，自动压缩同样显示在时间线。失败卡片展示错误原因和恢复结果；手动压缩后显示投影 token 估算，新增消息后回到模型 usage。

## 关键源码

- `Backend/src/OpenAgent.Core/Conversation/ConversationHistoryFactory.cs`
- `Backend/src/OpenAgent.Core/Conversation/TurnCompactionStrategy.cs`
- `Backend/src/OpenAgent.Core/Conversation/AuditedCompactionStrategy.cs`
- `Backend/src/OpenAgent.Core/Conversation/CompactionMessageMetadata.cs`
- `Backend/src/OpenAgent.Core/Conversation/ConversationSessionStore.cs`
- `Frontend/OpenAgent.Chat/src/composables/useConversationState.ts`
- `Frontend/OpenAgent.Chat/src/composables/useChatStreaming.ts`

验证见 [VALIDATION.md](VALIDATION.md)，来源见 [RESEARCH.md](RESEARCH.md)。
