# ConfigManagement - 编码约定

## 命名约定

### 接口命名

- 配置快照接口：`IConfigSnapshot`
- 配置提供者接口（外部）：`IAgentConfigProvider`（来自 Contracts 包）

### 类命名

- 快照实现类：`ConfigSnapshot`（internal，无接口前缀）
- 配置提供者：`ConfigProvider`（internal，实现外部接口）

### 方法命名

- 读取方法：`GetConfig<T>`、`GetVersion`
- 写入方法：`SetConfig<T>`、`SetVersion`
- 尝试读取：`TryGetConfig<T>`
- 批量写入：`SetFullConfig`
- 降级配置：`CreateMockFallbackConfig`
- 密钥注入：`EnrichWithSecureSecrets`
- 解析方法：`ResolveAllowMockAgent`、`ParseVersion`
- Redis 读取：`LoadFromRedisAsync`
- Snapshot 读取：`LoadFromSnapshot`

### 私有方法命名

- 辅助方法使用 `PascalCase`：`BuildCacheKey`、`BuildVersionKey`
- 转换方法使用 `To` 前缀：无此场景（ConfigProvider 不做类型转换）

### 字段命名

- 私有字段使用 `_` 前缀 + camelCase：`_cache`、`_lock`、`_redis`、`_environment`、`_configuration`、`_logger`、`_snapshot`、`_allowMockAgent`
- 静态只读字段使用 `PascalCase`：`CaseInsensitiveJsonOptions`

## 日志约定

### 日志级别

| 场景 | 级别 | 示例 |
|------|------|------|
| 从 Redis 加载配置 | Information | `"Agent config loaded from Redis and cached for agent {AgentId}"` |
| Mock 降级 | Information | `"No AgentId provided. Degrading to MockAgent (AllowMockAgent=true)."` |
| Snapshot 命中 | Debug | `"Agent config loaded from in-memory snapshot for agent {AgentId}"` |
| Redis 不可用 | Warning | `"Redis is not available. Entering island mode — skipping Redis config lookup for agent {AgentId}."` |
| 无配置 | Warning | `"No cached configuration available for agent {AgentId}."` |
| 反序列化失败 | Error | `"Failed to deserialize agent config from Redis for agent: {AgentId}"` |

### 结构化日志参数

- 使用 `{AgentId}`、`{Version}`、`{FrameworkType}` 等命名参数
- 日志消息为完整英文句子

## 错误处理约定

### 异常策略

- **无 agentId 调用**：抛出 `InvalidOperationException`，明确告知调用方使用正确的重载
- **Redis 异常**：不向上抛出，降级到下一级读取链
- **反序列化失败**：记录 Error 日志，返回 null 继续降级

### 降级策略

```
Snapshot → Redis → Mock → null
```

每级失败后静默降级，不抛出异常。

## 缓存 Key 命名约定

- 配置 key：`agent:{agentId}:config:{configType}`
- 版本 key：`agent:{agentId}:config:{configType}:version`
- 使用冒号 `:` 作为层级分隔符

## JSON 序列化约定

- 使用 `System.Text.Json`
- `PropertyNameCaseInsensitive = true`：忽略属性名大小写
- `JsonStringEnumConverter`：枚举序列化为字符串
- 静态共享 `JsonSerializerOptions` 实例

## DI 注册约定

- `ConfigSnapshot` 注册为 Singleton（具体类，非接口）
- `ConfigProvider` 注册为 `IAgentConfigProvider` 的 Singleton
- `ConfigProvider` 构造函数直接依赖 `ConfigSnapshot` 具体类（非接口）

## 环境变量约定

- 双下划线 `__` 格式：`LLM__APIKEY`（.NET 配置层级标准）
- 下划线 `_` 格式：`LLM_API_KEY`（兼容格式）
- 全大写：`ALLOW_MOCK_AGENT`
- 优先级：`__` 格式 > `_` 格式

## 线程安全约定

- `ConfigSnapshot.SetConfig` / `SetVersion` 使用 `lock(_lock)` 保护写入
- `ConfigSnapshot.GetConfig` / `GetVersion` 无锁读取（IMemoryCache.TryGetValue 线程安全）
- `ConfigProvider` 无状态，线程安全
