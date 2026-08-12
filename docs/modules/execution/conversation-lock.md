# Conversation Lock

`IConversationLock` 按 `{tenantId}:{conversationId}` 串行化请求。配置 Redis 时，
`RedisConversationLock` 使用 `SET NX` 获取带 TTL 的分布式锁，并在长推理期间以 Lua 校验所有者后
续租；锁获取失败时请求返回冲突，释放由 `IConversationLockHandle.DisposeAsync` 完成。未配置 Redis
时，`InMemoryConversationLock` 仅用于单个 Engine 进程的开发/测试降级。

跨实例写入正确性同时由分布式锁和数据库乐观并发保障：当前 EF Core Provider 以
`expectedVersion` 与并发令牌执行检查，冲突时不覆盖已提交的会话消息。

`IConversationLock` 不绑定 Redis；后续可以替换为数据库 advisory lock、Consul 等协调 Provider，
而不改变会话存储或业务契约。Redis 仅保存锁令牌和可失效热副本，不是会话或文件资产的持久化存储。
