# Redis — 约定

## Key 命名

- 会话记录：`conversation:{tenantId}:{conversationId}`
- Key 包含租户 ID，实现多租户隔离
- Key 前缀固定为 `conversation:`

## TTL 策略

- 会话记录 TTL 由 `ConversationStoreOptions.RedisTtlMinutes` 配置
- 默认 **30 分钟**
- 每次写入（Create/Append/UpdateStatus）刷新 TTL
- TTL 到期后数据从 SQL Server 冷归档恢复

## 序列化约定

- 使用 `System.Text.Json`
- `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- `WriteIndented = false`（紧凑格式）
- 整个 `ConversationRecord` 序列化为单个 String 值

## 乐观并发

- `AppendMessagesAsync` 和 `UpdateStatusAsync` 需要 `expectedVersion` 参数
- 版本不匹配时操作失败，返回 Conflict/false
- 成功操作后 `Version` 自增 1
- 调用方负责处理版本冲突（重新加载版本号后重试）

## 创建语义

- `CreateAsync` 使用 `When.NotExists`（NX 语义）
- Key 已存在时不覆盖，返回 false
- 防止并发创建同一会话

## 降级到 InMemory

- Redis 不可用时自动降级到 `InMemoryConversationStore`
- 降级触发条件：`GetDatabase()` 返回 null
- 降级后数据仅存在于内存，进程重启后丢失
- Redis 恢复后自动切回，不自动同步内存数据到 Redis

## 连接管理

- 使用 `IConnectionMultiplexer`（StackExchange.Redis）管理连接
- 连接由 DI 容器注入，`RedisConversationStore` 不负责创建
- 支持 Sentinel 和 Cluster 模式（由连接字符串配置）

## 错误处理

- 所有异常不向上传播
- 读取异常返回安全的默认值（空集合/null）
- 写入异常返回 false/Conflict
- 异常记录 Error 日志

## 性能指标

- 所有操作使用 `Stopwatch` 计时
- 在 `finally` 块中记录延迟
- 指标通过 `ConversationStoreMetrics` 收集
- 指标包括：命中率、延迟、失败计数、消息数
