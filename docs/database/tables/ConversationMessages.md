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
| TraceId | string? | 产生该消息的轮次追溯键（前端 X-Trace-Id；最大长度 256；历史消息为空） |
| MetadataJson | string? | 结构化元数据（jsonb，见下） |

## MetadataJson 形态（双形态窗口）

C# 契约类型为 `OpenAgent.Contracts.Conversation.ConversationMessageMetadata`：
`Files`（附件清单，`MessageFileMetadata(FileId, FileName, MediaType, Length, ObjectKey?)`）、
`Reasoning`（思考链文本）、`ExecutionStatus`（中止/失败状态，值为 `ConversationStatus` 字符串名）、
`ToolArguments`（工具参数原始 JSON 字符串）、`Extensions`（未知键逃生舱）。

**新形态（唯一写入形态）**：EF 写侧统一使用 `JsonSerializerDefaults.Web`（camelCase），与 Redis
热缓存序列化语义一致；`Extensions` 字典键不做命名改写，第三方键原样透传：

```json
{
  "files": [
    { "fileId": "file-001", "fileName": "notes.md", "mediaType": "text/markdown", "length": 12, "objectKey": "files/tenant-1/file-001" }
  ],
  "reasoning": "Inspect the uploaded file first.",
  "executionStatus": "Cancelled",
  "toolArguments": "{\"path\":\"report.md\"}",
  "extensions": { "CustomKey": "kept" }
}
```

**旧形态（历史存量，只读兼容）**：string→string 字典，PascalCase 键
（`Files` / `Reasoning` / `ToolArguments` / `ExecutionStatus`），其中 `Files` 的值是再序列化
一次的 JSON 数组（内层元素属性 camelCase/PascalCase 均可解析）：

```json
{
  "Files": "[{\"fileId\":\"file-001\",\"fileName\":\"notes.md\",\"mediaType\":\"text/markdown\",\"length\":12}]",
  "Reasoning": "Inspect the uploaded file first.",
  "ToolArguments": "{\"path\":\"report.md\"}",
  "ExecutionStatus": "Cancelled"
}
```

读取规则（`OpenAgent.Infrastructure.ConversationMessageMetadataJson`，大小写不敏感逐键解析，
两种形态统一覆盖）：

- 已知键映射到强类型属性；未知键进入 `Extensions` 原样保留（非字符串值保留原始 JSON 文本）。
- 解析失败不再静默丢失：单个键值解析失败时该键降级进 `Extensions` 并打 Warning 日志
  （EventId 5002）；整个载荷无法解析时以保留键 `__rawMetadataJson` 存入 `Extensions` 并打
  Warning 日志（EventId 5001）。
- 旧数据不迁移、不回填；归一化（PR-8d）滞后一个发布窗口另行决策。

