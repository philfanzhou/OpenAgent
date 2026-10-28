# Conversations

`openagent.conversations` 保存会话归属、状态、标题、时间戳和 `Version`。`Version` 是 EF Core 存储实现用于追加和状态变更的乐观并发边界。

`openagent.conversation_messages` 按 `(ConversationId, Sequence)` 唯一排序，保存角色、内容、工具调用信息、时间戳和可扩展 `MetadataJson(jsonb)`。assistant 终态还可保存 `PromptTokens`、`CompletionTokens`、`TotalTokens`、独立细分的 `CachedInputTokens`/`ReasoningTokens` 与 `ModelId`；这些列均可空，用于诚实表示旧历史、失败或 Provider usage 缺失。消息不嵌入会话 JSON，也不保存文件字节。

用户消息携带的 fileIds（请求层概念，非表列）在同一事务内解析为 `conversation_file_references` 与 `message_file_references` 行；前者支持会话级浏览，后者保证工作台能在准确的消息位置预览或下载文件。
