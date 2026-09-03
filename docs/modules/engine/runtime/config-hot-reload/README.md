# Config Update Model

配置热更新不再使用 Redis Pub/Sub、进程内 Snapshot 或 LLM Registry。

管理端更新先提交 PostgreSQL，再立即覆盖当前租户和资源对应的 Redis TTL 缓存。其他实例不依赖通知：缓存到期或未命中时直接从 PostgreSQL 回源。这样只有一个事实源，也不需要维护消息兼容、重放和本地快照失效逻辑。

一次执行在开始时解析 Agent 与所选 LLM Profile，并在该次执行内保持不变；后续请求读取最新可见配置。
