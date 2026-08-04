# Testing: 会话存储链路

## 测试策略

会话存储链路的测试围绕接口契约展开，确保所有实现（InMemory / Redis / DualWrite）行为一致。

## 单元测试

### InMemoryConversationStore

| 测试场景 | 验证点 |
|----------|--------|
| 创建新会话 | CreateAsync 返回 true，GetRecordAsync 可读回 |
| 创建重复会话 | CreateAsync 返回 false，原记录不变 |
| 追加消息 | AppendMessagesAsync 返回 Success，Version 自增 |
| 追加消息版本冲突 | expectedVersion 不匹配时返回 Conflict |
| 获取最近 N 条消息 | GetMessagesAsync 按 Sequence 升序返回，不超过 maxMessages |
| 更新状态 | UpdateStatusAsync 成功后 Status 变更 |
| 更新状态版本冲突 | expectedVersion 不匹配时返回 false |
| 不存在的会话 | GetRecordAsync 返回 null |
| 消息序号递增 | 追加后 Sequence 连续递增 |
| 列出会话 | ListConversationsAsync 按租户过滤，按 LastMessageAt 降序，支持分页 |
| 列出会话分页 | skip/take 正确应用 |
| 搜索会话 | SearchConversationsAsync 按关键词匹配消息内容 |
| 搜索大小写不敏感 | SearchConversationsAsync 不区分大小写 |
| 消息分页 | GetMessagesPagedAsync 按 skip/take 返回正确范围 |
| 消息幂等去重 | 相同 IdempotencyKey 的消息被跳过，SkippedDuplicateCount > 0 |

### RedisConversationStore

| 测试场景 | 验证点 |
|----------|--------|
| 序列化与反序列化 | ConversationRecord / ConversationMessage 完整往返（CamelCase 命名策略） |
| TTL 设置 | 写入后 Redis 键带有正确过期时间 |
| Key 格式 | conversation:{tenantId}:{conversationId} |
| CreateAsync NX 语义 | 仅在 key 不存在时写入 |
| Metrics 追踪 | 读写操作记录延迟和命中率指标 |
| 并发写入冲突 | 多线程同时追加时版本冲突正确返回 |
| Redis 不可用 | GetDatabase 返回 null 时优雅降级 |

### DualWriteConversationStore

| 测试场景 | 验证点 |
|----------|--------|
| 热冷双写成功 | Redis 和 SQL Server 均写入成功 |
| 冷归档失败补偿 | SQL Server 写入失败时，Redis 仍成功，记录 Error 日志 |
| 冷归档重试 | SQL Server 临时失败后按 RetryCount + 指数退避重试 |
| 热存储失败 | Redis 写入失败时整体失败，不写冷存储 |
| 冷归档异步执行 | 不阻塞主路径返回 |
| 消息归档补偿 | AppendMessagesAsync 后即发即弃归档消息行，失败不影响热存储 |
| EnableColdArchive=false 跳过归档 | 冷归档禁用时仅走热存储 |
| 列出会话委托 | ListConversationsAsync 委托给热存储 |
| 搜索会话委托 | SearchConversationsAsync 委托给热存储 |
| 消息分页委托 | GetMessagesPagedAsync 委托给热存储 |

### SqlServerConversationRepository

| 测试场景 | 验证点 |
|----------|--------|
| 自动建表 | 首次写入时创建 ConversationRecords、ConversationMessages 表和索引，以及 TVP 类型 |
| 构造函数校验 | 连接字符串为空时抛出 InvalidOperationException |
| MERGE INTO 新记录 | 不存在时 INSERT |
| MERGE INTO 已有记录 | 已存在时 UPDATE |
| 批量消息写入 | TVP 批量插入消息行 |
| 消息加载租户隔离 | LoadMessagesAsync 按 TenantId + ConversationId 过滤 |
| 重试逻辑 | 临时错误时按 RetryCount + 指数退避重试 |
| EnableColdArchive=false | 不执行写入 |

### SqliteConversationRepository

| 测试场景 | 验证点 |
|----------|--------|
| 自动建表 | 首次写入时创建 ConversationRecords、ConversationMessages 表和索引 |
| 构造函数校验 | 连接字符串为空时抛出 InvalidOperationException |
| UPSERT 新记录 | 不存在时 INSERT |
| UPSERT 已有记录 | 已存在时 UPDATE（ON CONFLICT） |
| 批量消息写入 | 事务逐行 INSERT OR IGNORE |
| 消息加载租户隔离 | LoadMessagesAsync 按 TenantId + ConversationId 过滤 |
| 重试逻辑 | 临时错误时按 RetryCount + 指数退避重试 |
| EnableColdArchive=false | 不执行写入 |
| 列出会话 | ListConversationsAsync 按租户过滤，按 LastMessageAt 降序，支持分页 |
| 搜索会话 | SearchConversationsAsync 按关键词匹配消息内容 |

### ConversationQueryService

| 测试场景 | 验证点 |
|----------|--------|
| 列出会话仅热存储 | 无冷归档时直接返回热存储结果 |
| 列出会话热冷合并 | 热 + 冷结果按 ConversationId 去重，热存储版本优先 |
| 列出会话冷归档失败 | 冷归档异常时优雅降级到热存储结果 |
| 获取单个会话热存储命中 | 热存储有记录时直接返回 |
| 获取单个会话冷归档回退 | 热存储无记录时查冷归档，同时加载消息体 |
| 获取单个会话均未找到 | 热存储和冷归档均无记录时返回 null |
| 消息分页热存储有数据 | 直接返回热存储分页结果 |
| 消息分页冷归档回退 | 热存储为空时查冷归档 LoadMessagesAsync + 内存分页 |
| 搜索会话仅热存储 | 无冷归档时直接返回热存储搜索结果 |
| 搜索会话热冷合并 | 热 + 冷搜索结果按 ConversationId 去重 |
| 搜索会话冷归档失败 | 冷归档异常时优雅降级到热存储结果 |

### ConversationStoreMetrics

| 测试场景 | 验证点 |
|----------|--------|
| 计数器递增 | Hit/Miss/ReadFailure/WriteFailure 正确递增 |
| 线程安全 | 并发调用计数器不丢失 |
| LogSnapshot | 输出包含所有指标值 |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整读写周期 | 创建 → 追加 → 读取 → 状态更新 → 验证一致性 |
| 流式取消写回 | 取消后 partial assistant 消息落库，状态为 Cancelled |
| 流式失败写回 | 失败后 partial assistant 消息落库，状态为 Failed |
| 非流式成功写回 | 最终 assistant 消息落库 |
| 版本冲突重试 | 冲突后重新加载并重试追加成功 |
| 未知角色消息 | 跳过不回放，记录告警日志 |

## 验收口径

判断会话存储是否满足执行侧要求：

- [ ] conversationId 能稳定透传到 Core
- [ ] 历史消息能按 Sequence 顺序回放
- [ ] 未知角色不会污染执行链路
- [ ] 非流式和流式都能写回 user / assistant / tool
- [ ] 取消和失败状态能正确落库
- [ ] Redis / 双写场景下版本冲突可重试
- [ ] 会话列表查询按租户隔离，支持分页
- [ ] 会话搜索按关键词匹配消息内容，不区分大小写
- [ ] 消息分页查询支持 skip/take
- [ ] 热/冷合并查询正确去重，热存储版本优先
- [ ] 冷归档不可用时查询优雅降级
- [ ] 消息幂等去重（IdempotencyKey）防止重复写入
