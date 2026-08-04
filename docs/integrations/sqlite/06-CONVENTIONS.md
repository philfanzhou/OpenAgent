# SQLite — 约定

## 表结构

- 元数据表：`ConversationRecords`，主键 `ConversationId`
- 消息表：`ConversationMessages`，复合主键 `(ConversationId, Sequence)`
- 自动建表：首次归档时检查并创建（`CREATE TABLE IF NOT EXISTS`）
- 索引：
  - `IX_Records_Tenant_User`（TenantId, UserId, UpdatedAt）
  - `IX_Records_Tenant_Agent`（TenantId, AgentId, UpdatedAt）
  - `IX_Messages_Tenant_Time`（TenantId, Timestamp）

## 连接字符串

- 从 `ConversationStoreOptions.ColdArchiveConnectionString` 读取
- 为空时构造函数抛出 `InvalidOperationException`
- 使用 `Data Source=path/to/file.db` 格式
- 建议使用 `Microsoft.Data.Sqlite` NuGet 包

## 重试策略

- 写入失败时采用指数退避重试
- 初始延迟：`ColdArchiveRetryDelayMs`（默认 1000ms）
- 每次重试延迟翻倍：`delayMs *= 2`
- 最多重试：`ColdArchiveRetryCount`（默认 3 次）
- 重试仍失败时异常向上传播，由 `ArchiveWithCompensationAsync` 捕获
- 重试事件记录 Warning 日志（当前次数、延迟）

## 写入模式

- 仅在 `DualWrite` 模式下写入 SQLite
- 会话记录：INSERT ON CONFLICT DO UPDATE
- 消息行：事务逐行 INSERT OR IGNORE
- 写入为异步 Fire-and-Forget
- 不阻塞 Redis 主写入
- 租户隔离：所有查询强制 `WHERE TenantId = $TenantId`

## 日期时间

- 所有时间字段以 ISO 8601 字符串存储（`DateTimeOffset.ToString("O")`）
- 读取时通过 `DateTimeOffset.Parse()` 还原
- 精度与 `DateTimeOffset` 一致（含时区信息）

## 数据一致性

- 热存储（Redis）为权威数据源
- 冷归档为异步写入，可能短暂落后
- 冷归档失败时热存储数据一致，冷存储需要补偿
- 补偿日志明确标记："Hot store is consistent. Cold store needs compensation."

## 消息序列化

- 消息以行级方式存储在 `ConversationMessages` 表
- 每条消息的 `Metadata` 字段独立 JSON 序列化到 `MetadataJson` 列
- 使用 `System.Text.Json`，CamelCase 命名策略

## 性能指标

- 归档成功：`RecordColdArchiveSuccess()`
- 归档失败：`RecordColdArchiveFailure()`
- 归档延迟：`RecordColdArchiveLatency(ms)`
- 使用 `Stopwatch` 计时
