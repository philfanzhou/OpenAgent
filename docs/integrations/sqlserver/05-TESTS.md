# SQL Server — 测试清单

## 单元测试

### SqlServerConversationRepository

- `ArchiveAsync` EnableColdArchive 为 false 时跳过
- `ArchiveAsync` 正确执行 MERGE INTO Upsert
- `ArchiveMessagesAsync` TVP 批量写入消息行
- `LoadMessagesAsync` 按 TenantId + ConversationId 加载
- `GetRecordAsync` 加载记录 + 完整消息列表
- `EnsureInitializedAsync` 首次调用时创建表、索引和 TVP 类型
- `EnsureInitializedAsync` 后续调用跳过建表
- `UpsertRecordAsync` 新记录插入
- `UpsertRecordAsync` 已有记录更新
- `BulkInsertMessagesAsync` MERGE INTO + TVP 实现
- `RetryAsync` 首次成功不重试
- `RetryAsync` 失败后指数退避重试
- `RetryAsync` 重试耗尽后异常向上传播
- `ColdArchiveConnectionString` 为空时构造函数抛出异常

### DualWriteConversationStore

- `CreateAsync` 热存储成功后触发冷归档
- `CreateAsync` 热存储失败时跳过冷归档
- `AppendMessagesAsync` 热存储成功后触发冷归档
- `AppendMessagesAsync` 热存储失败时返回失败结果
- `UpdateStatusAsync` 热存储成功后触发冷归档
- `UpdateStatusAsync` 热存储失败时返回 false
- `GetRecordAsync` 仅从热存储读取
- `GetMessagesAsync` 仅从热存储读取
- 冷归档异常不阻塞主流程
- EnableColdArchive 为 false 时不触发冷归档

## 集成测试

- SQL Server 连接与断线场景
- 自动建表验证（ConversationRecords + ConversationMessages + TVP）
- MERGE INTO 幂等性验证
- TVP 批量消息写入性能
- DualWrite 端到端流程
