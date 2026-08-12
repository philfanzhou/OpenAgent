# Conversation Lock

`IConversationLock` 当前默认由 `InMemoryConversationLock` 实现，只在单个 Engine 进程内按
`{tenantId}:{conversationId}` 串行化请求。锁获取失败时请求返回冲突，释放由
`IConversationLockHandle.DisposeAsync` 完成。

跨实例写入正确性不依赖该内存锁：`PostgresConversationStore` 以 `expectedVersion` 和 EF Core
并发令牌执行乐观并发检查，冲突时不覆盖已提交的会话消息。

后续如需跨实例推理串行化，应以独立协调服务实现 `IConversationLock`，不能恢复 Redis 作为会话
或文件资产的持久化存储。
