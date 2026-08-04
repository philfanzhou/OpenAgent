# API: 会话存储链路

## IConversationStore

会话存储的核心接口，所有存储实现均遵循此契约。

### GetMessagesAsync

获取会话的最近 N 条消息，按 Sequence 升序返回。

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
    string tenantId,
    string conversationId,
    int maxMessages,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| maxMessages | int | 最多返回消息条数 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：按 Sequence 升序排列的消息列表，最多 `maxMessages` 条。

---

### GetRecordAsync

获取完整会话记录（含元数据和全部消息）。

```csharp
Task<ConversationRecord?> GetRecordAsync(
    string tenantId,
    string conversationId,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：会话记录，不存在时返回 `null`。

---

### CreateAsync

创建新会话记录。如果已存在则返回 `false`。

```csharp
Task<bool> CreateAsync(
    ConversationRecord record,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| record | ConversationRecord | 待创建的会话记录 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：创建成功返回 `true`，已存在返回 `false`。

---

### AppendMessagesAsync

追加消息到已有会话。使用乐观锁：如果 `expectedVersion` 与存储版本不匹配则失败。成功后 Version 自增 1。

```csharp
Task<AppendResult> AppendMessagesAsync(
    string tenantId,
    string conversationId,
    int expectedVersion,
    IReadOnlyList<ConversationMessage> messages,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| expectedVersion | int | 期望的当前版本号 |
| messages | IReadOnlyList\<ConversationMessage\> | 待追加的消息列表 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：`AppendResult`，包含 Success / NewVersion / NewMessageCount / ConflictReason。

---

### UpdateStatusAsync

更新会话状态（Running / Completed / Failed / Cancelled）。

```csharp
Task<bool> UpdateStatusAsync(
    string tenantId,
    string conversationId,
    ConversationStatus status,
    int expectedVersion,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| status | ConversationStatus | 目标状态 |
| expectedVersion | int | 期望的当前版本号 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：更新成功返回 `true`，版本冲突返回 `false`。

---

### ListConversationsAsync

列出指定租户的会话，按 `LastMessageAt` 降序排列，支持分页。返回的记录不含消息体（`Messages` 为空）。

```csharp
Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
    string tenantId,
    int skip,
    int take,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| skip | int | 跳过前 N 条记录 |
| take | int | 最多返回 N 条记录 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：按 `LastMessageAt` 降序排列的会话记录列表（不含消息体）。

---

### SearchConversationsAsync

按关键词搜索会话消息内容，不区分大小写。返回的记录不含消息体。

```csharp
Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
    string tenantId,
    string keyword,
    int skip,
    int take,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| keyword | string | 搜索关键词 |
| skip | int | 跳过前 N 条记录 |
| take | int | 最多返回 N 条记录 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：匹配关键词的会话记录列表（不含消息体）。

---

### GetMessagesPagedAsync

分页获取会话消息，按 `Sequence` 升序排列。

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
    string tenantId,
    string conversationId,
    int skip,
    int take,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| skip | int | 跳过前 N 条消息 |
| take | int | 最多返回 N 条消息 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：按 `Sequence` 升序排列的消息列表。

---

## IConversationQueryService

查询侧门面服务，实现热存储 + 冷归档合并查询策略。热存储优先，冷归档补充，按 `ConversationId` 去重（热存储版本优先）。

### ListConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
    string tenantId, int skip, int take, CancellationToken cancellationToken = default);
```

**行为**：合并热存储和冷归档结果，去重后按 `LastMessageAt` 降序排列，应用分页。冷归档查询失败时优雅降级到热存储结果。

### GetConversationAsync

```csharp
Task<ConversationRecord?> GetConversationAsync(
    string tenantId, string conversationId, CancellationToken cancellationToken = default);
```

**行为**：优先查热存储，未找到时回退冷归档（同时加载消息体）。

### GetMessagesPagedAsync

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
    string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default);
```

**行为**：优先查热存储，热存储为空时回退冷归档 `LoadMessagesAsync` + 内存分页。

### SearchConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
    string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default);
```

**行为**：合并热存储和冷归档搜索结果，去重后按 `LastMessageAt` 降序排列，应用分页。

---

## IConversationRepository（查询扩展）

冷归档仓储接口新增的查询方法：

### ListConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
    string tenantId, int skip, int take, CancellationToken cancellationToken = default);
```

### SearchConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
    string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default);
```

---

## ConversationMessage.IdempotencyKey

消息幂等键，用于防止重试导致消息重复写入。相同 `IdempotencyKey` 的消息在 `AppendMessagesAsync` 中会被去重跳过。

```csharp
public string? IdempotencyKey { get; set; }
```

---

## HTTP 端点（Engine Host）

### 用户端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/conversations?skip=0&take=20` | 列出会话（自动过滤 `IsDeletedByUser=0`） |
| GET | `/conversations/search?keyword=xxx&skip=0&take=20` | 按标题搜索会话（仅查 `Title` 字段，不碰消息表） |
| GET | `/conversations/{conversationId}` | 获取单个会话 |
| GET | `/conversations/{conversationId}/messages?skip=0&take=50` | 分页获取消息 |
| DELETE | `/conversations/{conversationId}` | 软删除会话（设置 `IsDeletedByUser=1`，数据保留供审计） |

> 注意：`/conversations/search` 必须注册在 `/conversations/{conversationId}` 之前，避免路由冲突。

### 审计端点（独立路由前缀，需管理员角色）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/audit/conversations?skip=0&take=20` | 列出全量会话（含已删除） |
| GET | `/audit/conversations/search?keyword=xxx&startDate=...&endDate=...&skip=0&take=20` | 搜索消息内容（强制时间范围，结果脱敏） |
| GET | `/audit/conversations/{conversationId}` | 获取单个会话详情（含已删除，消息内容脱敏） |

审计端点约束：
- 返回的消息内容需脱敏处理（邮箱、手机号、身份证等 PII 字段）
- 搜索接口强制要求 `startDate` / `endDate` 时间范围参数，利用 `IX_Messages_Tenant_Time` 索引限定扫描范围
- 审计查询可跨 `ConversationMessages` + `ConversationMessagesArchive` 两表 UNION 查询

---

## 调用方使用模式

### 创建新会话

```csharp
var record = new ConversationRecord
{
    ConversationId = conversationId,
    TenantId = tenantId,
    UserId = userId,
    AgentId = agentId,
    TraceId = traceId
};
await store.CreateAsync(record);
```

### 追加消息（含冲突重试）

```csharp
var result = await store.AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages);
if (!result.Success)
{
    // 重新加载记录，重新分配序号，重试一次
    var record = await store.GetRecordAsync(tenantId, conversationId);
    // ... 重新构建 messages 并重试
}
```

### 更新会话状态

```csharp
await store.UpdateStatusAsync(tenantId, conversationId, ConversationStatus.Failed, expectedVersion);
```
