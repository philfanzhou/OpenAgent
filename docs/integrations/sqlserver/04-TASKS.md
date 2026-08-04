# SQL Server — 任务清单

## 已完成

- [x] `SqlServerConversationRepository` 实现（IConversationRepository 接口）
- [x] 自动建表（`ConversationRecords` + `ConversationMessages` + TVP 类型 + 索引）
- [x] `DualWriteConversationStore` 双写模式实现
- [x] 读取仅从热存储（Redis）
- [x] 写入先热后冷（Fire-and-Forget）
- [x] 冷归档失败补偿日志
- [x] 行级消息表（`ConversationMessages`）+ TVP 批量写入
- [x] 租户隔离（`LoadMessagesAsync` 强制 `TenantId` 过滤）
- [x] 指数退避重试（`ColdArchiveRetryCount` / `ColdArchiveRetryDelayMs`）
- [x] `ColdArchiveProvider` 配置 + .NET 8 Keyed DI 选择
- [x] `EnableColdArchive` 配置开关
- [x] `ColdArchiveConnectionString` 配置
- [x] 性能指标收集（`ConversationStoreMetrics`）

## 待办

- [ ] 冷归档补偿机制（自动重试失败的归档）
- [ ] 从冷归档恢复到热存储的流程
- [ ] 大批量消息归档性能优化
- [ ] 索引优化建议（按查询模式调整）
- [ ] **ConversationRecords 表新增字段**：Title、IsDeletedByUser、DeletedAt、ArchivedAt + 对应索引
- [ ] **ConversationMessagesArchive 归档表建表**：与 ConversationMessages 结构一致，启用页压缩
- [ ] **软删除实现**：SoftDeleteAsync 方法 + 用户侧查询自动过滤 IsDeletedByUser=0
- [ ] **会话标题生成**：首轮消息截取初始标题 + 异步 LLM 摘要更新（失败告警不阻塞）
- [ ] **数据分层迁移任务**：IHostedService 定时扫描超期会话，同事务迁移消息到归档表
- [ ] **审计端点**：独立 /audit/conversations 路由，含已删除会话，消息内容脱敏，搜索强制时间范围
- [ ] **审计搜索跨表查询**：UNION ConversationMessages + ConversationMessagesArchive
