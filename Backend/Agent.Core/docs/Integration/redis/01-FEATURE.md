# Redis — 功能概述

## 核心能力

Agent.Core 使用 Redis 作为会话热存储，提供高频读写能力。通过 `IConversationStore` 接口抽象，`RedisConversationStore` 为主要实现。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `IConversationStore` | `Agent.Contracts/Conversation/IConversationStore.cs` | 会话存储统一接口 |
| `RedisConversationStore` | `src/Core/Conversation/Store/RedisConversationStore.cs` | Redis 会话存储实现 |
| `ConversationRecord` | `Agent.Contracts/Conversation/` | 会话记录模型 |
| `ConversationMessage` | `Agent.Contracts/Conversation/` | 会话消息模型 |
| `AppendResult` | `Agent.Contracts/Conversation/IConversationStore.cs` | 追加操作结果 |
| `ConversationStoreOptions` | `Agent.Contracts/Conversation/ConversationStoreOptions.cs` | 存储配置选项 |
| `ConversationStoreMetrics` | — | 存储指标收集 |

## 功能范围

- 会话记录的创建（`CreateAsync`，NX 语义）
- 会话消息的追加（`AppendMessagesAsync`，乐观并发）
- 会话记录的读取（`GetRecordAsync`）
- 会话消息的读取（`GetMessagesAsync`，支持最近 N 条）
- 会话状态更新（`UpdateStatusAsync`，乐观并发）
- TTL 自动过期
- 性能指标收集（命中率、延迟、失败计数）

## IConversationStore 核心方法

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string tenantId, string conversationId, int maxMessages, CancellationToken ct = default);
Task<ConversationRecord?> GetRecordAsync(string tenantId, string conversationId, CancellationToken ct = default);
Task<bool> CreateAsync(ConversationRecord record, CancellationToken ct = default);
Task<AppendResult> AppendMessagesAsync(string tenantId, string conversationId, int expectedVersion, IReadOnlyList<ConversationMessage> messages, CancellationToken ct = default);
Task<bool> UpdateStatusAsync(string tenantId, string conversationId, ConversationStatus status, int expectedVersion, CancellationToken ct = default);
```
