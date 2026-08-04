# Redis — 测试清单

## 单元测试

### RedisConversationStore CRUD

- `CreateAsync` 正确创建会话记录，设置 TTL
- `CreateAsync` Key 已存在时返回 false
- `GetRecordAsync` 正确读取会话记录
- `GetRecordAsync` Key 不存在时返回 null
- `GetMessagesAsync` 返回最近 N 条消息
- `GetMessagesAsync` 消息数不足 N 条时返回全部
- `GetMessagesAsync` Key 不存在时返回空集合

### 乐观并发

- `AppendMessagesAsync` 版本匹配时成功追加
- `AppendMessagesAsync` 版本不匹配时返回 Conflict
- `AppendMessagesAsync` 记录不存在时返回 Conflict
- `AppendMessagesAsync` 成功后 Version 自增 1
- `AppendMessagesAsync` 成功后 MessageCount 更新
- `AppendMessagesAsync` 成功后 UpdatedAt 更新
- `AppendMessagesAsync` 成功后 LastMessageAt 更新
- `UpdateStatusAsync` 版本匹配时成功更新
- `UpdateStatusAsync` 版本不匹配时返回 false
- `UpdateStatusAsync` 记录不存在时返回 false

### 序列化

- JSON 使用 CamelCase 命名策略
- 序列化/反序列化往返一致性

### Key 格式

- Key 格式为 `conversation:{tenantId}:{conversationId}`

### 错误处理

- Redis 连接失败时返回安全的默认值
- 读取异常时返回空集合/null
- 写入异常时返回 false/Conflict

## 集成测试

- Redis 连接与断线场景
- DualWrite 场景下的 Redis 写入
- TTL 过期后数据不可读
- 并发写入的乐观并发冲突
