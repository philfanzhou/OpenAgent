# SQLite — 任务清单

## 已完成

- [x] `SqliteConversationRepository` 实现（IConversationRepository 接口）
- [x] 自动建表（`ConversationRecords` + `ConversationMessages` + 索引）
- [x] INSERT ON CONFLICT Upsert
- [x] 事务逐行 INSERT OR IGNORE 批量消息写入
- [x] 租户隔离（`LoadMessagesAsync` 强制 `TenantId` 过滤）
- [x] 指数退避重试（`ColdArchiveRetryCount` / `ColdArchiveRetryDelayMs`）
- [x] `EnableColdArchive` 配置开关
- [x] `ColdArchiveConnectionString` 配置
- [x] `ColdArchiveProvider = "Sqlite"` 配置
- [x] .NET 8 Keyed DI 注册
- [x] 性能指标收集（`ConversationStoreMetrics`）

## 待办

- [ ] 冷归档补偿机制（自动重试失败的归档）
- [ ] 从冷归档恢复到热存储的流程
- [ ] SQLite 并发写入优化（WAL 模式）
- [ ] 连接池优化（SQLite 单写锁）
- [ ] **ConversationRecords 表新增字段**：Title、IsDeletedByUser、DeletedAt、ArchivedAt
- [ ] **软删除实现**：SoftDeleteAsync 方法 + 用户侧查询自动过滤 IsDeletedByUser=0
- [ ] **会话标题生成**：首轮消息截取初始标题 + 异步 LLM 摘要更新
- [ ] **数据保留策略**：SQLite 场景数据量较小，可考虑简化方案（如定期 VACUUM + 按需清理归档表）
