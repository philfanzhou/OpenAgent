# ConversationMessages

`openagent.conversation_messages` 是独立表（schema `openagent`），主键 `MessageId`，按 `(ConversationId, Sequence)` 唯一排序，外键级联删除指向 `openagent.conversations` 表。消息不嵌入会话 JSON。

## 字段定义（C# 契约）

| 字段名 | C# 类型 | 说明 |
|--------|---------|------|
| MessageId | string | 消息唯一标识（最大长度 64） |
| ConversationId | string | 所属会话 ID（最大长度 64，外键指向 conversations） |
| Sequence | int | 消息序号（按会话内顺序递增） |
| Role | string | 角色：user / assistant / tool（最大长度 32） |
| Content | string | 消息内容 |
| ToolCallId | string? | 关联的工具调用 ID（最大长度 256） |
| ToolName | string? | 关联的工具名称（最大长度 256） |
| IdempotencyKey | string? | 幂等键（最大长度 256） |
| Timestamp | DateTimeOffset | 消息时间戳（timestamptz） |
| MetadataJson | string? | 扩展元数据（jsonb） |
