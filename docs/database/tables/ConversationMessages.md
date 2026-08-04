# ConversationMessages

ConversationMessage 不是独立的数据库表，而是以 JSON 数组形式嵌入在 [ConversationRecords.MessagesJson](./ConversationRecords.md) 列中。

## 字段定义（C# 契约）

| 字段名 | C# 类型 | 说明 |
|--------|---------|------|
| MessageId | string | 消息唯一标识（GUID 格式） |
| Sequence | int | 消息序号（从 1 开始递增） |
| Role | string | 角色：user / assistant / tool |
| Content | string | 消息内容 |
| ToolCallId | string? | 关联的工具调用ID |
| ToolName | string? | 关联的工具名称 |
| Timestamp | DateTimeOffset | 消息时间戳 |
| Metadata | Dictionary<string, string>? | 扩展元数据 |
