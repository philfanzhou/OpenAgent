# 06-CONVENTIONS — Redis 连接管理编码约定

> 关联文档：[03-DESIGN.md](./03-DESIGN.md) | [05-TESTS.md](./05-TESTS.md)

---

本文档从实际代码中提取编码约定，确保后续开发保持一致性。

## 1. 命名约定

### 1.1 接口命名

| 约定 | 示例 | 说明 |
|------|------|------|
| 内部接口以 `I` 前缀 | `IRedisConnectionProvider` | 标准 C# 接口命名 |
| Provider 后缀 | `IRedisConnectionProvider` | 提供者模式统一使用 Provider 后缀 |
| Registry 后缀 | `IEngineRegistry` | 注册表模式统一使用 Registry 后缀 |
| Registrar 后缀 | `RedisSkillRegistrar` | 启动时执行注册的服务使用 Registrar 后缀 |

### 1.2 类命名

| 约定 | 示例 | 说明 |
|------|------|------|
| 实现类以技术前缀区分 | `RedisConnectionProvider`, `FakeRedisConnectionProvider` | 前缀标识底层技术（Redis/Fake） |
| `sealed` 用于无继承类 | `RedisConnectionProvider : sealed` | 无派生需求的实现类标记 sealed |
| 测试替身以 `Fake` 前缀 | `FakeRedisConnectionProvider` | 非 Mock 框架的手写替身统一用 Fake |
| 健康检查以 `HealthCheck` 后缀 | `RedisHealthCheck` | 实现 `IHealthCheck` 的类统一后缀 |

### 1.3 字段命名

| 约定 | 示例 | 说明 |
|------|------|------|
| 私有字段下划线前缀 | `_redis`, `_logger`, `_sync`, `_connection` | 标准 C# 私有字段命名 |
| 布尔字段下划线前缀 | `_disposed`, `_isRegistered`, `_portSet` | 布尔字段同样使用下划线前缀 |
| 静态只读使用 PascalCase | `RetryInterval`, `RetryDelay`, `ConnectTimeout` | `private static readonly` 字段使用 PascalCase |
| 锁对象命名 | `_sync`, `_lock` | 同步锁对象统一命名 |

### 1.4 Redis Key 命名

| 约定 | 示例 | 说明 |
|------|------|------|
| 冒号分隔层级 | `engine:registry:{engineId}` | 三级结构：`领域:操作:标识` |
| published:index 后缀 | `agent:published:index` | 已发布索引 SET 统一后缀 |
| registry 前缀 | `skill:registry:{skillName}` | 注册表数据统一前缀 |
| config 前缀 | `agent:config:{agentId}` | 配置数据统一前缀 |

### 1.5 Pub/Sub Channel 命名

| 约定 | 示例 | 说明 |
|------|------|------|
| 冒号分隔 | `agent:config:updates` | 与 Key 命名风格一致 |
| 动词后缀区分类型 | `updates`（当前）、`changed`（Legacy） | 当前频道用 `updates`，Legacy 频道用 `changed` |
| 常量定义 | `CurrentUpdatesChannel`, `LegacyChannels` | 频道名定义为常量 |

## 2. 日志约定

### 2.1 日志级别

| 级别 | 使用场景 | 示例 |
|------|----------|------|
| `LogInformation` | 正常业务流程 | `"Redis connection established."`、`"Engine registered with ID: {EngineId}"` |
| `LogWarning` | 可恢复的异常/降级 | `"Failed to connect to Redis. Engine will continue in island mode..."` |
| `LogWarning(ex, ...)` | 捕获的异常（不影响运行） | `"Failed to register engine in Redis. Continuing in island mode."` |
| `LogError` | 不可恢复/需关注的异常 | `"Failed to deserialize agent config from Redis..."` |
| `LogDebug` | 调试信息/跳过逻辑 | `"RedisSkillRegistrar: Redis not available. Skipping..."` |

### 2.2 日志消息格式

| 约定 | 示例 | 说明 |
|------|------|------|
| 结构化日志参数 | `"Engine registered with ID: {EngineId} at {Host}:{Port}"` | 使用命名占位符，非字符串插值 |
| 包含上下文标识 | `{EngineId}`, `{AgentId}`, `{Channel}`, `{Host}:{Port}` | 关键业务标识必须出现在日志中 |
| 类名前缀（FakeRedis） | `"FakeRedis: In-memory store initialized"` | FakeRedis 日志以 `FakeRedis:` 前缀区分 |
| 类名前缀（Registrar） | `"RedisSkillRegistrar: Redis not available..."` | Registrar 日志以类名前缀区分 |

## 3. 错误处理约定

### 3.1 孤岛模式降级

```csharp
// 模式：连接不可用时返回安全默认值，不抛异常
var db = GetDatabase();
return db != null
    ? db.StringGetAsync(key, flags)
    : Task.FromResult(RedisValue.Null);
```

- 读操作 → 返回空值（`RedisValue.Null`、`Array.Empty<RedisValue>()`、`TimeSpan.Zero`）
- 写操作 → 返回 `false`
- `GetDatabase()` → 抛出 `InvalidOperationException`（明确告知调用方连接不可用）
- `GetServer()` → 返回 `null`
- `Subscribe()` → 空操作

### 3.2 异常捕获模式

```csharp
// 模式：catch 不向上传播，记录日志后继续
try
{
    await _redis.StringSetAsync(key, json, ttl);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to register engine in Redis. Continuing in island mode.");
    _isRegistered = false;
}
```

- 上层服务（Registry、Registrar、ConfigProvider）捕获所有 Redis 异常
- 使用 `LogWarning(ex, ...)` 记录异常，不向上传播
- 设置降级状态（如 `_isRegistered = false`）

### 3.3 NotSupportedException 使用

```csharp
// FakeRedisConnectionProvider 中不支持的方法
public IDatabase GetDatabase(int database = 0)
{
    throw new NotSupportedException(
        "FakeRedisConnectionProvider does not support GetDatabase. Use StringGetAsync/StringSetAsync instead.");
}
```

- 消息中说明原因和替代方案
- 仅用于接口方法确实无法实现的场景

## 4. 线程安全约定

`RedisConnectionProvider` 是薄包装，线程安全由底层 `IConnectionMultiplexer` 保证，
Engine 不再使用 `lock(_sync)` 双重检查锁或 `_disposed` 标记。

## 5. DI 注册约定

### 5.1 Singleton 生命周期

```csharp
services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
```

- Redis 连接提供者统一注册为 Singleton
- 测试中通过 DI 注入 `FakeRedisConnectionProvider` 替代

### 5.2 条件注册

```csharp
// 直接注册 RedisConnectionProvider，无需前缀判断
services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
```

- 生产环境使用 StackExchange.Redis
- 测试环境使用 FakeRedisConnectionProvider（内存 Mock）

## 6. 配置约定

### 6.1 Options 模式

```csharp
services.Configure<HeartbeatOptions>(configuration.GetSection("Heartbeat"));
```

- 使用 `IOptionsMonitor<T>` 消费配置（支持热更新）
- 配置类命名为 `{Feature}Options`
- 配置键与类属性名保持一致

### 6.2 安全默认值

```csharp
var ttlSeconds = Math.Max(_options.CurrentValue.RegistryTtlSeconds, 1);
var intervalSeconds = Math.Max(_options.CurrentValue.IntervalSeconds, 1);
```

- 使用 `Math.Max` 确保配置值不低于最小值
- 防止零值或负值导致异常

## 7. 测试约定

### 7.1 测试替身

- 优先使用手写 `Fake` 替身（非 Mock 框架）
- Fake 类实现完整接口，提供可控属性（如 `IsAvailableValue`、`PingException`）
- 辅助方法命名：`SetString(key, value)`

### 7.2 测试方法命名

```
{Method}_{Scenario}_{ExpectedBehavior}
```

示例：
- `Returns_degraded_when_redis_not_available`
- `ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis`
- `GetConfigAsync_loads_from_redis_when_snapshot_miss`

### 7.3 测试文件组织

- 测试类与源类同名 + `Tests` 后缀
- 测试文件路径镜像源码路径：`test/.../HealthChecks/RedisHealthCheckTests.cs`
