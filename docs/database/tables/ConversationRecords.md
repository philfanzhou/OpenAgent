# Conversations

`openagent.conversations` 保存会话归属、状态、标题、时间戳和 `Version`。`Version` 是 EF Core 存储实现用于追加和状态变更的乐观并发边界。

`openagent.conversation_messages` 按 `(ConversationId, Sequence)` 唯一排序，保存角色、内容、工具调用信息、时间戳和可扩展 `MetadataJson(jsonb)`。assistant 终态还可保存 `PromptTokens`、`CompletionTokens`、`TotalTokens`、独立细分的 `CachedInputTokens`/`ReasoningTokens` 与 `ModelId`；这些列均可空，用于诚实表示旧历史、失败或 Provider usage 缺失。消息不嵌入会话 JSON，也不保存文件字节。

用户消息携带的 fileIds（请求层概念，非表列）在同一事务内解析为 `conversation_file_references` 与 `message_file_references` 行；前者支持会话级浏览，后者保证工作台能在准确的消息位置预览或下载文件。

会话的 `ContextSummariesJson` 保存压缩审计与模型历史投影；原始消息仍保留在 `conversation_messages`。新投影使用 `ProjectionVersion=1`，`SourceEndSequence` 是投影包含的原始消息末序号，后续按原始序号追加消息。旧 JSON 缺少版本时按 0 解释，仅旧投影执行历史重叠兼容修复。版本字段位于现有 JSON 中，本次不需要表结构迁移。
