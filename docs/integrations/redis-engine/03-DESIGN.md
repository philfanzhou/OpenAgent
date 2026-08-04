# 03-DESIGN — Redis 连接管理设计与架构

> 关联文档：[01-FEATURE.md](./01-FEATURE.md) | [02-SPEC.md](./02-SPEC.md) | [06-CONVENTIONS.md](./06-CONVENTIONS.md)

---

## 1. 架构总览

```
┌─────────────────────────────────────────────────────────┐
│                    ServiceCollection                      │
│  ┌─────────────────────────────────────────────────────┐ │
│  │  IRedisConnectionProvider (Singleton)                │ │
│  │  ┌──────────────────┐                                │ │
│  │  │ RedisConnection  │  测试使用 Mock Redis           │ │
│  │  │ Provider         │  （FakeRedisConnectionProvider）│ │
│  │  │ (StackExchange)  │                                │ │
│  │  └──────────────────┘                                │ │
│  └─────────────────────────────────────────────────────┘ │
│                          │                                │
│          ┌───────────────┼───────────────┐                │
│          ▼               ▼               ▼                │
│  ┌──────────────┐ ┌────────────┐ ┌──────────────┐        │
│  │ ConfigProvider│ │RedisRegistry│ │HotReloadService│      │
│  └──────────────┘ └────────────┘ └──────────────┘        │
│          │               │               │                │
│  ┌──────────────┐ ┌────────────┐ ┌──────────────┐        │
│  │RedisSkill    │ │RedisLlm    │ │RedisRag      │        │
│  │Registrar     │ │Registrar   │ │Registrar     │        │
│  └──────────────┘ └────────────┘ └──────────────┘        │
│                          │                                │
│  ┌───────────────────────┴───────────────────────┐        │
│  │              RedisHealthCheck                  │        │
│  └───────────────────────────────────────────────┘        │
└─────────────────────────────────────────────────────────┘
```

## 2. 核心接口

### 2.1 IRedisConnectionProvider

**文件路径：** `src/Engine/Abstractions/IRedisConnectionProvider.cs`

```csharp
internal interface IRedisConnectionProvider : IDisposable
{
    bool IsAvailable { get; }
    IServer? GetServer(int database = 0);
    IDatabase GetDatabase(int database = 0);

    Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None);
    Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None);
    Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None);
    Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None);
    Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None);
    Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None);

    RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None);
    void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler);
}
```

**设计决策：**
- `internal` 可见性：仅在 Engine 程序集内使用，不对外暴露
- 继承 `IDisposable`：确保连接资源可被释放
- 同时提供同步（`StringGet`）和异步（`StringGetAsync`）方法：部分消费方（如 `RedisSkillRegistrar`）在 `IHostedService.StartAsync` 中使用同步调用
- `Subscribe` 为同步方法：订阅是长生命周期操作，不需要 await

## 3. 实现类

### 3.1 RedisConnectionProvider（薄包装实现）

**文件路径：** `src/Engine/Redis/RedisConnectionProvider.cs`

**类签名：** `internal sealed class RedisConnectionProvider : IRedisConnectionProvider`

**依赖注入：**
- `IConnectionMultiplexer?` — 由 Agent.Core 的 `AddAgentCore` 注册（可能为 null，孤岛模式）

**设计：** 薄包装，不再自管连接。`ConnectionMultiplexer` 自动重连，`AbortOnConnectFail=false`
让命令排队等待。连接可能为 null（孤岛模式）——所有读写操作降级为空/false/零。

**关键字段：**

| 字段 | 类型 | 用途 |
|------|------|------|
| `_connection` | `IConnectionMultiplexer?` | Core 注册的连接，可能为 null（孤岛模式） |

**请求处理流程：**

1. 每个方法读取 `_connection` 字段
2. 连接为 null → 返回安全默认值（Null / false / 空数组 / TimeSpan.Zero / 抛 InvalidOperationException）
3. 连接非 null → 委托给 `GetDatabase()` / `GetSubscriber()`

**孤岛模式返回值映射：**

| 方法 | Redis 可用时 | Redis 不可用时（孤岛模式） |
|------|-------------|--------------------------|
| `StringGetAsync` | `db.StringGetAsync()` | `Task.FromResult(RedisValue.Null)` |
| `StringSetAsync` | `db.StringSetAsync(When.Always)` | `Task.FromResult(false)` |
| `KeyDeleteAsync` | `db.KeyDeleteAsync()` | `Task.FromResult(false)` |
| `SetMembersAsync` | `db.SetMembersAsync()` | `Task.FromResult(Array.Empty<RedisValue>())` |
| `SetAddAsync` | `db.SetAddAsync()` | `Task.FromResult(false)` |
| `PingAsync` | `db.PingAsync()` | `Task.FromResult(TimeSpan.Zero)` |
| `StringGet` | `db.StringGet()` | `RedisValue.Null` |
| `GetDatabase` | `db` | `InvalidOperationException` |
| `GetServer` | `IServer` | `null` |
| `Subscribe` | `subscriber.Subscribe()` | 空操作 |

### 3.2 测试实现（Mock Redis）

**文件路径：** `test/.../Fakes/FakeRedisConnectionProvider.cs`

**类签名：** `internal class FakeRedisConnectionProvider : IRedisConnectionProvider`

**设计目的：** 在单元测试和集成测试中，使用内存字典模拟 Redis 操作，无需外部 Redis 依赖。

## 4. DI 注册

**文件路径：** `src/Engine/Extensions/ServiceCollectionExtensions.cs`

**注册逻辑：**

```csharp
services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
```

**说明：** `RedisConnectionProvider` 是 Core 注册的 `IConnectionMultiplexer` 的薄包装。
`IConnectionMultiplexer` 由 `Agent.Core` 的 `AddAgentCore` 以 `TryAddSingleton` 注册（连接失败时返回 null，
进入孤岛模式）。测试中通过 DI 注入 `FakeRedisConnectionProvider` 替代。

## 5. 消费方

### 5.1 ConfigProvider

**文件路径：** `src/Engine/Config/ConfigProvider.cs`

**Redis 操作：**
- `StringGetAsync($"agent:config:{agentId}")` — 读取 Agent 配置 JSON
- `SetMembersAsync("agent:published:index")` — 获取已发布 Agent ID 列表
- `StringGetAsync($"agent:config:{agentId}")` — 列表查询时逐个读取配置

**孤岛模式行为：** Redis 不可用时跳过 Redis 查找，返回 null 或 Mock 降级配置。

### 5.2 RedisRegistry

**文件路径：** `src/Engine/Registry/RedisRegistry.cs`

**Redis 操作：**
- `StringSetAsync($"engine:registry:{engineId}", json, ttl)` — 注册/心跳（WRITE）
- `KeyDeleteAsync($"engine:registry:{engineId}")` — 注销

**实现接口：** `IEngineRegistry`

### 5.3 HotReloadService

**文件路径：** `src/Engine/Reload/HotReloadService.cs`

**Redis 操作：**
- `Subscribe(channel, handler)` — 订阅配置变更频道
- `StringGet($"agent:config:{agentId}")` — 收到通知后从 Redis 刷新配置

**订阅频道：**
- `agent:config:updates` — 当前结构化频道
- `agent:config:changed` — Legacy
- `skill:registry:changed` — Legacy
- `llm:registry:changed` — Legacy
- `rag:registry:changed` — Legacy
- `engine:config:changed` — Legacy

### 5.4 RedisSkillRegistrar / RedisLlmRegistrar / RedisRagRegistrar

**文件路径：**
- `src/Engine/Redis/RedisSkillRegistrar.cs`
- `src/Engine/Redis/RedisLlmRegistrar.cs`
- `src/Engine/Redis/RedisRagRegistrar.cs`

**Redis 操作模式一致：**
1. `SetMembersAsync("{type}:published:index")` — 获取已发布条目列表
2. `StringGet($"{type}:registry:{id}")` — 逐个读取条目 JSON
3. 反序列化后注册到内存 Registry

**孤岛模式行为：** `IsAvailable == false` 时直接跳过，记录 Debug 日志。

### 5.5 RedisHealthCheck

**文件路径：** `src/Engine/Redis/RedisHealthCheck.cs`

**逻辑：**
1. `IsAvailable == false` → `Degraded`
2. `PingAsync()` 成功 → `Healthy`
3. `PingAsync()` 异常 → `Unhealthy`

## 6. Redis Key 模式

| Key 模式 | 读写 | 用途 | 消费方 |
|----------|------|------|--------|
| `engine:registry:{engineId}` | **WRITE** | Engine 注册 JSON（含 TTL） | RedisRegistry |
| `agent:config:{agentId}` | READ | Agent 配置 JSON | ConfigProvider, HotReloadService |
| `agent:published:index` | READ | 已发布 Agent ID（SET） | ConfigProvider |
| `skill:registry:{skillName}` | READ | 技能定义 JSON | RedisSkillRegistrar |
| `skill:published:index` | READ | 已发布技能名（SET） | RedisSkillRegistrar |
| `llm:registry:{profileId}` | READ | LLM 配置 JSON | RedisLlmRegistrar |
| `llm:published:index` | READ | 已发布 LLM 配置（SET） | RedisLlmRegistrar |
| `rag:registry:{instanceId}` | READ | RAG 实例 JSON | RedisRagRegistrar |
| `rag:published:index` | READ | 已发布 RAG 实例（SET） | RedisRagRegistrar |

## 7. 配置项

**文件路径：** `src/Engine/Models/HeartbeatOptions.cs`

| 配置键 | 类型 | 默认值 | 用途 |
|--------|------|--------|------|
| `ConnectionStrings:Redis` | `string?` | `"localhost:6379"` | Redis 连接字符串 |
| `Heartbeat:IntervalSeconds` | `int` | `10` | 心跳间隔（秒） |
| `Heartbeat:RetryDelaySeconds` | `int` | `5` | 心跳失败重试延迟（秒） |
| `Heartbeat:RegistryTtlSeconds` | `int` | `30` | 注册键 TTL（秒） |
| `Heartbeat:AdvertisedHost` | `string?` | `null` | 对外广播的主机名 |
| `Heartbeat:AdvertisedPort` | `int?` | `null` | 对外广播的端口 |

## 8. 数据依赖

```
IRedisConnectionProvider
    ├── ConfigProvider ──────── agent:config:{id}, agent:published:index
    ├── RedisRegistry ──────── engine:registry:{id} (WRITE)
    ├── HotReloadService ───── Subscribe + agent:config:{id}
    ├── RedisSkillRegistrar ── skill:published:index, skill:registry:{name}
    ├── RedisLlmRegistrar ──── llm:published:index, llm:registry:{id}
    ├── RedisRagRegistrar ──── rag:published:index, rag:registry:{id}
    └── RedisHealthCheck ───── PingAsync
```
