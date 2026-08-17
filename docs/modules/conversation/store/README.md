# 会话持久化

`IConversationStore` 是数据库无关契约。当前 `OpenAgent.Infrastructure` 使用 EF Core + PostgreSQL
作为持久化实现，不支持其他数据库 Provider。

生产链路使用写穿透组合：PostgreSQL 先完成会话、消息与文件引用的事务性写入，成功后把完整会话
写入 Redis 热副本。读取优先命中 Redis，未命中时从 PostgreSQL 回填。Redis 从不作为事实源，
缓存失败不会回滚已提交的数据库写入。

`IConversationLock` 是独立于存储的协调契约：Redis 已配置时使用带租约心跳的分布式锁，保证同一
`{tenantId}:{conversationId}` 在多个 Engine 实例间串行执行；未配置 Redis 的单实例开发环境使用
进程内锁。两种锁都不承担数据存储职责。
