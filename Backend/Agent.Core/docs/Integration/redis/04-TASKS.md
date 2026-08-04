# Redis — 任务清单

## 已完成

- [x] `IConversationStore` 接口定义（5 个方法）
- [x] `AppendResult` 结果模型（Ok/Conflict 静态工厂方法）
- [x] `ConversationStoreOptions` 配置选项
- [x] `RedisConversationStore` 完整实现
- [x] Key 格式 `conversation:{tenantId}:{conversationId}`
- [x] JSON 序列化（CamelCase 命名策略）
- [x] 乐观并发控制（Version 字段）
- [x] Create 使用 `When.NotExists`（NX 语义）
- [x] TTL 由 `RedisTtlMinutes` 配置（默认 30 分钟）
- [x] 性能指标收集（`ConversationStoreMetrics`）
- [x] `Stopwatch` 计时记录延迟
- [x] 错误处理（不向上传播异常）

## 待办

- [ ] Redis 集群模式下的兼容性说明
- [ ] 大消息量场景的性能优化（当前整体序列化）
- [ ] 消息分页读取支持
