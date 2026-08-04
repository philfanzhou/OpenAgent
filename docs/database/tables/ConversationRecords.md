# ConversationRecords 表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| ConversationId | NVARCHAR(128) | PRIMARY KEY | - | 会话唯一标识 |
| TenantId | NVARCHAR(128) | NOT NULL | - | 租户ID，用于数据隔离 |
| UserId | NVARCHAR(128) | NOT NULL | - | 用户ID |
| AgentId | NVARCHAR(128) | NULL | - | Agent ID |
| TraceId | NVARCHAR(128) | NULL | - | 链路追踪ID |
| Version | INT | NOT NULL | 1 | 乐观并发版本号 |
| Status | INT | NOT NULL | 0 | 会话状态（0=Running, 1=Completed, 2=Failed, 3=Cancelled） |
| CreatedAt | DATETIMEOFFSET | NOT NULL | - | 创建时间 |
| UpdatedAt | DATETIMEOFFSET | NOT NULL | - | 最后更新时间 |
| LastMessageAt | DATETIMEOFFSET | NOT NULL | - | 最后消息时间 |
| MessageCount | INT | NOT NULL | 0 | 消息总数 |
| MessagesJson | NVARCHAR(MAX) | NOT NULL | - | 消息列表 JSON（ConversationMessage 数组） |

## 索引

| 索引名 | 字段 | 类型 |
|--------|------|------|
| PK_ConversationRecords | ConversationId | PRIMARY KEY |
| IX_ConversationRecords_Tenant_User | TenantId, UserId, UpdatedAt | NONCLUSTERED |
| IX_ConversationRecords_Tenant_Agent | TenantId, AgentId, UpdatedAt | NONCLUSTERED |

## MessagesJson 格式

ConversationMessage 对象数组：

```json
[
  {
    "messageId": "a1b2c3d4",
    "sequence": 1,
    "role": "user",
    "content": "你好",
    "toolCallId": null,
    "toolName": null,
    "timestamp": "2025-01-01T00:00:00+00:00",
    "metadata": null
  }
]
```

## 并发控制

使用 Version 字段实现乐观并发。AppendMessages 和 UpdateStatus 操作需要传入 expectedVersion，版本不匹配时返回冲突结果。

## Redis 热存储

Redis 中以 JSON 字符串存储完整的 ConversationRecord，key 格式为 `conversation:{tenantId}:{conversationId}`，TTL 由 `ConversationStoreOptions.RedisTtlMinutes` 配置。
