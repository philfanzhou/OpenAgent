# Redis Integration

Agent.Core 使用 Redis 作为会话热存储，通过 `IConversationStore` 接口抽象，`RedisConversationStore` 为主要实现。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 会话 CRUD | 创建（NX 语义）、读取、消息追加、状态更新 |
| 乐观并发 | `Version` 字段，`AppendMessagesAsync` / `UpdateStatusAsync` 需 `expectedVersion` |
| TTL 自动过期 | `RedisTtlMinutes` 配置（默认 30 分钟），每次写入刷新 |
| 指标收集 | 命中率、延迟、失败计数（`ConversationStoreMetrics`）|
| 降级 | Redis 不可用时自动降级到 `InMemoryConversationStore` |

## Architecture
```text
IConversationStore
  └── RedisConversationStore
        ├── Key: conversation:{tenantId}:{conversationId}
        ├── Value: ConversationRecord JSON（String 类型）
        └── TTL: 每次写入刷新
```

## Current Status
**Implemented** — 完整实现，包含乐观并发、TTL、指标收集和错误处理。

## Limits
- 整个 `ConversationRecord` 序列化为单个 String 值，大消息量场景有优化空间
- 无消息分页读取支持
- 降级到 InMemory 后数据仅存在于内存，重启后丢失

## Source
- Implementation: `src/Core/Conversation/Store/RedisConversationStore.cs`
- Contracts: `Agent.Contracts/Conversation/IConversationStore.cs`, `ConversationStoreOptions.cs`
- Tests: `test/OpenAgent.Core.Tests/Conversation/RedisConversationStoreTests.cs`
