# Conversations 与 ConversationMessages

`openagent.conversations` 保存会话归属、状态、标题、时间戳和 `Version`。`Version` 是 EF Core 存储实现用于追加和状态变更的乐观并发边界。

`openagent.conversation_messages` 按 `(ConversationId, Sequence)` 唯一排序，保存角色、内容、工具调用信息、时间戳和可扩展 `MetadataJson(jsonb)`。消息不嵌入会话 JSON，也不保存文件字节。

用户消息的 `FileIds` 同一事务写入 `conversation_file_references` 和 `message_file_references`；前者支持会话级浏览，后者保证工作台能在准确的消息位置预览或下载文件。
