# Architecture: 会话存储链路

## 存储分层

存储链路采用三级渐进式架构，按配置自动选择实现：

```
┌─────────────────────────────────────────────────┐
│              IConversationStore                  │
│                 (热存储接口层)                     │
├─────────────────────────────────────────────────┤
│  InMemoryConversationStore                      │
│  ├─ ConcurrentDictionary<string, ConversationRecord> │
│  └─ 适用：开发/测试环境，无 Redis 配置时回退       │
├─────────────────────────────────────────────────┤
│  RedisConversationStore                         │
│  ├─ Redis String 存储会话记录（JSON 序列化）       │
│  ├─ 带 Metrics 追踪（读写延迟/命中率）            │
│  ├─ TTL 过期策略（RedisTtlMinutes，默认 30 分钟） │
│  ├─ Key 格式：conversation:{tenantId}:{conversationId} │
│  └─ 适用：生产环境，仅热存储                      │
├─────────────────────────────────────────────────┤
│  DualWriteConversationStore                     │
│  ├─ 热路径：Redis（同 RedisConversationStore）    │
│  ├─ 冷路径：IConversationRepository              │
│  │   ├─ SqlServerConversationRepository          │
│  │   └─ SqliteConversationRepository             │
│  ├─ MERGE INTO / UPSERT + 指数退避重试           │
│  ├─ 冷归档失败时补偿：热存储仍成功，记录告警       │
│  └─ 适用：生产环境，EnableColdArchive=true        │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│           IConversationRepository                │
│              (冷存储接口层)                        │
├─────────────────────────────────────────────────┤
│  SqlServerConversationRepository                │
│  ├─ 行级消息表：ConversationMessages              │
│  ├─ 批量写入：TVP (Table-Valued Parameter)       │
│  ├─ 自动建表（EnsureInitializedAsync）            │
│  └─ MERGE INTO + 指数退避重试                    │
├─────────────────────────────────────────────────┤
│  SqliteConversationRepository                   │
│  ├─ 行级消息表：ConversationMessages              │
│  ├─ 批量写入：事务逐行 INSERT OR IGNORE          │
│  ├─ 自动建表（EnsureInitializedAsync）            │
│  └─ UPSERT + 指数退避重试                        │
└─────────────────────────────────────────────────┘
```

## 选择逻辑

由 ServiceExtensions.AddAgentCore 根据配置决定：

### 热存储选择

```
未配置 ConversationStore:RedisConnectionString → InMemoryConversationStore
配置 Redis + EnableColdArchive=false → RedisConversationStore
配置 Redis + EnableColdArchive=true  → DualWriteConversationStore(Redis, Repository)
```

### 冷存储选择

冷归档实现通过 .NET 8 Keyed DI 注册，根据 `ColdArchiveProvider` 配置动态选取：

```
ColdArchiveProvider="SqlServer" → SqlServerConversationRepository
ColdArchiveProvider="Sqlite"    → SqliteConversationRepository
```

两个实现始终注册在 DI 容器中（连接仅在首次使用时建立）：
```csharp
services.AddKeyedSingleton<IConversationRepository, SqlServerConversationRepository>("SqlServer");
services.AddKeyedSingleton<IConversationRepository, SqliteConversationRepository>("Sqlite");
```

## 读写流程

### 读取流程

1. 执行前检查上下文是否包含 `ConversationId` 和 `TenantId`
2. 调用 `GetRecordAsync` 加载完整会话记录
3. 若会话不存在，调用 `CreateAsync` 创建新会话主记录
4. 若会话存在，取最近 `MaxHistoryMessages` 条历史消息回放
5. 未知角色消息跳过并记录告警日志

### 写回流程

- **非流式成功**：写回最终 `assistant` 消息
- **流式成功**：流结束后写回 `assistant` 消息
- **流式取消**：写回已产生的 partial `assistant`，状态标记 `Cancelled`
- **流式失败**：写回已产生的 partial `assistant`，状态标记 `Failed`
- **非流式失败/取消**：保留本轮已产生消息并更新状态

## 并发控制

采用版本号驱动的乐观并发：

1. `AppendMessagesAsync` 携带 `expectedVersion`
2. 存储层比对 `expectedVersion` 与当前 `record.Version`
3. 匹配则追加消息，`Version` 自增 1
4. 不匹配则返回 `AppendResult.Conflict`，携带 `ConflictReason`
5. 调用方收到冲突后重新加载记录并重试一次，重试时重新分配消息序号

## 冷归档

### IConversationRepository

冷归档将消息规范化为行级存储，包含两张表：

- **ConversationRecords**：会话元数据（ConversationId 为主键）
- **ConversationMessages**：消息行级表（ConversationId + Sequence 为复合主键），每条消息一行

冷归档操作：
- `ArchiveAsync`：UPSERT 会话元数据
- `ArchiveMessagesAsync`：批量写入消息行
- `GetRecordAsync`：读取记录 + 加载全部消息
- `LoadMessagesAsync`：按租户和会话 ID 加载消息（强制 tenantId 隔离）

### DualWriteConversationStore 补偿

- 热存储（Redis）写入成功即视为操作成功
- 冷归档失败时记录 Error 日志
- 不向调用方抛出冷归档异常
- 冷归档异步执行（`_ = ArchiveWithCompensationAsync`），不阻塞主路径

### 重试策略

- MERGE INTO / UPSERT 操作支持指数退避重试（delayMs *= 2）
- 最多重试 ColdArchiveRetryCount 次
- 归档失败时通过 ConversationStoreMetrics 记录失败指标

## 配置

所有存储行为由 `ConversationStoreOptions` 控制，配置节名称为 `ConversationStore`：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| MaxHistoryMessages | 20 | 执行侧历史消息窗口大小 |
| RedisTtlMinutes | 30 | Redis 会话记录 TTL |
| RedisConnectionString | null | Redis 连接字符串，为空则使用 InMemory |
| EnableColdArchive | true | 是否启用数据库冷归档 |
| ColdArchiveConnectionString | null | 数据库连接字符串 |
| ColdArchiveProvider | SqlServer | 冷归档提供器：SqlServer 或 Sqlite |
| ColdArchiveRetryCount | 3 | 冷归档写入重试次数 |
| ColdArchiveRetryDelayMs | 1000 | 冷归档写入重试延迟（基础值，指数退避） |
| MessageRetentionDays | 90 | 消息活跃期保留天数，超期迁移到归档消息表 |
| ArchiveMigrationIntervalMinutes | 60 | 数据分层迁移任务执行间隔（分钟） |
| ArchiveMigrationBatchSize | 100 | 每次迁移批量处理的最大会话数 |
| TitleTruncateLength | 50 | 会话标题截取的最大字符数 |
| EnableTitleSummarization | true | 是否启用 LLM 异步生成会话摘要标题 |

## 软删除与审计保留

### 软删除机制

用户删除会话时不物理删除数据，仅设置软删除标记：

- 设置 `IsDeletedByUser = true` + `DeletedAt = now`
- 通过 `UpdateStatusAsync` 或专用 `SoftDeleteAsync` 方法写入
- DualWrite 同步到冷归档

### 可见性隔离

| 场景 | 过滤条件 | 说明 |
|------|---------|------|
| 用户侧 List/Search | `IsDeletedByUser = 0` | 用户看不到已删除的会话 |
| 审计侧 List/Search | 无 `IsDeletedByUser` 过滤 | 审计接口可查全量，含已删除 |
| 审计侧消息内容 | 脱敏处理 | 审计端点返回的消息内容需脱敏 |

### 审计端点

独立的审计 API 端点，与用户端点隔离：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/audit/conversations?skip=0&take=20` | 列出全量会话（含已删除） |
| GET | `/audit/conversations/search?keyword=xxx&skip=0&take=20` | 搜索全量会话消息内容（需时间范围） |
| GET | `/audit/conversations/{conversationId}` | 获取单个会话详情（含已删除） |

审计端点返回的消息内容需脱敏处理（如邮箱、手机号、身份证等 PII 字段）。

## 会话标题生成

### 降级策略

```
会话创建时
  → 取首轮用户消息 Content 前 TitleTruncateLength 字符作为初始 Title
  → 此步骤必定成功，不依赖 LLM

首轮 LLM 响应后（异步）
  → EnableTitleSummarization=true 时，调用 LLM 生成摘要标题
  → 成功 → 更新 Title 字段
  → 失败 → 记录告警日志，保留截取标题，不影响业务
```

### 约束

- 初始标题生成不依赖任何外部服务，确保必定成功
- LLM 摘要标题为异步 fire-and-forget，不阻塞主流程
- LLM 调用失败时告警但不重试不阻塞
- Title 字段最大长度由 `TitleTruncateLength` 控制

## 数据分层管理

### 分层结构

冷归档层按数据活跃度分两层，同库不同表：

```
ConversationRecords（会话元数据，永久保留）
  ├── 不分层，行数远小于消息表（1:20+ 比例）
  └── 含 IsDeletedByUser / ArchivedAt 字段

ConversationMessages（活跃消息，默认保留 90 天）
  ├── 高频读写，索引齐全
  └── 数据量可控

ConversationMessagesArchive（归档消息，超期迁移）
  ├── 只写不读（审计调取时查）
  ├── 启用页压缩（Page Compression），空间降 50-70%
  └── 仅需 ConversationId + TenantId + Timestamp 索引
```

### 迁移机制

后台定时任务（IHostedService）按 `ArchiveMigrationIntervalMinutes` 间隔执行：

1. 扫描 `ConversationRecords` 中 `ArchivedAt < now - MessageRetentionDays` 的会话
2. 每批最多处理 `ArchiveMigrationBatchSize` 个会话
3. 对每个会话，同事务内：
   - `INSERT INTO ConversationMessagesArchive SELECT * FROM ConversationMessages WHERE ConversationId = @id`
   - `DELETE FROM ConversationMessages WHERE ConversationId = @id`
4. 元数据记录保留在 `ConversationRecords`，不迁移
5. 记录迁移日志和 Metrics

### 数据量控制

按 1000 活跃用户、日均 10 轮对话、每轮 20 条消息估算：

| 层级 | 周期 | 行数估算 | 空间估算 |
|------|------|---------|---------|
| Redis 热存储 | 30 分钟 TTL | ~2 万条 | 可控 |
| ConversationMessages 主表 | 90 天 | ~1800 万行 | ~5-10 GB |
| ConversationMessagesArchive | 永久 | 持续增长（压缩后） | 可控 |
| ConversationRecords | 永久 | ~90 万行/年 | < 1 GB |
