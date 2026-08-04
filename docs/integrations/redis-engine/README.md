
## Feature


## 核心用户故事

**作为** Engine 服务，**我希望**拥有一个具备自动重试和孤岛模式降级的弹性 Redis 连接，**以便**在 Redis 不可用时仍能继续处理本地请求。

## 功能概述

Engine 服务通过 Redis 实现配置热加载、引擎注册/心跳、技能/LLM/RAG 注册发现以及 Pub/Sub 配置变更通知。Redis 连接管理层提供两种连接提供者——基于 StackExchange.Redis 的生产实现和基于原始 TCP/RESP 协议的轻量测试实现——并统一通过 `IRedisConnectionProvider` 接口暴露，确保在 Redis 不可用时系统能以"孤岛模式"安全降级。

## 关键能力

| 能力 | 说明 |
|------|------|
| 弹性连接 | 连接失败后自动重试（5 秒间隔），不阻塞启动 |
| 孤岛模式降级 | Redis 不可用时，读操作返回空值/null，写操作返回 false，服务不中断 |
| 单一实现策略 | 生产环境使用 StackExchange.Redis；测试使用 FakeRedisConnectionProvider（内存 Mock） |
| 连接生命周期事件 | 监听 ConnectionFailed / ConnectionRestored / ErrorMessage 事件并记录日志 |
| 统一接口 | 生产使用 `RedisConnectionProvider`，测试使用 `FakeRedisConnectionProvider`，均实现 `IRedisConnectionProvider` |
| 健康检查 | 通过 `RedisHealthCheck` 暴露 Degraded / Healthy / Unhealthy 三级状态 |
 — 功能规格与验收标准
- [03-DESIGN.md](./03-DESIGN.md) — 设计与架构
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试用例
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 编码约定

## Specification


> 关联文档：[01-FEATURE.md](./01-FEATURE.md) | [03-DESIGN.md](./03-DESIGN.md) | [05-TESTS.md](./05-TESTS.md)

---

## 1. 功能需求（FR）

### FR-01: IRedisConnectionProvider 接口定义

| 编号 | 需求 | 来源签名 |
|------|------|----------|
| FR-01.1 | 暴露 `bool IsAvailable` 属性，指示 Redis 连接是否可用 | `IRedisConnectionProvider.IsAvailable` |
| FR-01.2 | 提供 `IServer? GetServer(int database = 0)` 方法获取 Redis 服务器实例 | `IRedisConnectionProvider.GetServer` |
| FR-01.3 | 提供 `IDatabase GetDatabase(int database = 0)` 方法获取数据库实例 | `IRedisConnectionProvider.GetDatabase` |
| FR-01.4 | 提供 `Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags)` 异步字符串读取 | `IRedisConnectionProvider.StringGetAsync` |
| FR-01.5 | 提供 `Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry, CommandFlags flags)` 异步字符串写入（支持 TTL） | `IRedisConnectionProvider.StringSetAsync` |
| FR-01.6 | 提供 `Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags)` 异步键删除 | `IRedisConnectionProvider.KeyDeleteAsync` |
| FR-01.7 | 提供 `Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags)` 异步集合成员查询 | `IRedisConnectionProvider.SetMembersAsync` |
| FR-01.8 | 提供 `Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags)` 异步集合添加 | `IRedisConnectionProvider.SetAddAsync` |
| FR-01.9 | 提供 `Task<TimeSpan> PingAsync(CommandFlags flags)` 异步延迟探测 | `IRedisConnectionProvider.PingAsync` |
| FR-01.10 | 提供 `RedisValue StringGet(RedisKey key, CommandFlags flags)` 同步字符串读取 | `IRedisConnectionProvider.StringGet` |
| FR-01.11 | 提供 `void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)` 订阅 Pub/Sub 频道 | `IRedisConnectionProvider.Subscribe` |
| FR-01.12 | 接口继承 `IDisposable` | `IRedisConnectionProvider : IDisposable` |

### FR-02: RedisConnectionProvider — 薄包装实现

`RedisConnectionProvider` 是 Core 注册的 `IConnectionMultiplexer` 的薄包装，
不再自管连接、不再 Lazy 连接、不再 5 秒节流重连、不再挂事件处理器。
`ConnectionMultiplexer` 自动重连，`AbortOnConnectFail=false` 让命令排队等待（由 Core 负责）。
连接可能为 null（孤岛模式）——所有读写操作降级为空/false/零。

| 编号 | 需求 |
|------|------|
| FR-02.1 | 构造函数注入 `IConnectionMultiplexer?`（由 Agent.Core 的 `AddAgentCore` 注册） |
| FR-02.2 | 连接为 null 时进入孤岛模式，不抛异常 |
| FR-02.3 | `IsAvailable` 返回 `_connection is { IsConnected: true }`（纯探测，不触发重连） |
| FR-02.4 | 孤岛模式下 `StringGetAsync` 返回 `RedisValue.Null` |
| FR-02.5 | 孤岛模式下 `StringSetAsync` 返回 `false`（使用 `When.Always` 标志） |
| FR-02.6 | 孤岛模式下 `KeyDeleteAsync` 返回 `false` |
| FR-02.7 | 孤岛模式下 `SetMembersAsync` 返回空数组 `Array.Empty<RedisValue>()` |
| FR-02.8 | 孤岛模式下 `SetAddAsync` 返回 `false` |
| FR-02.9 | 孤岛模式下 `PingAsync` 返回 `TimeSpan.Zero` |
| FR-02.10 | 孤岛模式下 `StringGet` 返回 `RedisValue.Null` |
| FR-02.11 | `GetDatabase(int)` 在连接不可用时抛出 `InvalidOperationException` |
| FR-02.12 | `GetServer(int)` 在连接不可用时返回 `null` |
| FR-02.13 | `Subscribe` 在连接不可用时为空操作（不抛异常） |
| FR-02.14 | `Dispose` 为空操作：`IConnectionMultiplexer` 生命周期由 DI 容器管理 |

以下能力已在批次二移除，不再存在：

- 从 `IConfiguration` 读取连接字符串、`AbortOnConnectFail=false`、`ConnectTimeout` 配置（由 Core 负责）
- Lazy 初始化、`_nextRetryAt` 5 秒节流重连、`lock(_sync)` 线程安全
- `ConnectionFailed` / `ConnectionRestored` / `ErrorMessage` 事件订阅
- 新连接建立时旧连接事件移除与 Dispose
- `ThrowIfDisposed` / `ObjectDisposedException`

### FR-03: DI 注册策略

| 编号 | 需求 |
|------|------|
| FR-03.1 | 注册 `RedisConnectionProvider` 为 `IRedisConnectionProvider` Singleton |
| FR-03.2 | 测试中通过 DI 注入 `FakeRedisConnectionProvider` 替代 |

### FR-05: 健康检查

| 编号 | 需求 |
|------|------|
| FR-05.1 | `RedisHealthCheck` 在 `IsAvailable == false` 时返回 `Degraded` |
| FR-05.2 | `RedisHealthCheck` 在 `PingAsync` 成功时返回 `Healthy` |
| FR-05.3 | `RedisHealthCheck` 在 `PingAsync` 抛出异常时返回 `Unhealthy`（含异常详情） |

---

## 2. 验收标准（AC）

### AC-01: RedisHealthCheck 健康检查

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-01.1 | Redis 不可用时返回 Degraded 状态 | `RedisHealthCheckTests.Returns_degraded_when_redis_not_available` |
| AC-01.2 | Ping 成功时返回 Healthy 状态 | `RedisHealthCheckTests.Returns_healthy_when_ping_succeeds` |
| AC-01.3 | Ping 抛异常时返回 Unhealthy 状态且包含异常 | `RedisHealthCheckTests.Returns_unhealthy_when_ping_throws` |

### AC-02: RedisConnectionProvider 孤岛模式

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-02.1 | 无连接字符串时进入孤岛模式，不抛异常 | [当前无测试覆盖] |
| AC-02.2 | 孤岛模式下 StringGetAsync 返回 RedisValue.Null | [当前无测试覆盖] |
| AC-02.3 | 孤岛模式下 StringSetAsync 返回 false | [当前无测试覆盖] |
| AC-02.4 | 孤岛模式下 KeyDeleteAsync 返回 false | [当前无测试覆盖] |
| AC-02.5 | 孤岛模式下 SetMembersAsync 返回空数组 | [当前无测试覆盖] |
| AC-02.6 | 孤岛模式下 SetAddAsync 返回 false | [当前无测试覆盖] |
| AC-02.7 | 孤岛模式下 PingAsync 返回 TimeSpan.Zero | [当前无测试覆盖] |
| AC-02.8 | 孤岛模式下 StringGet 返回 RedisValue.Null | [当前无测试覆盖] |
| AC-02.9 | 孤岛模式下 GetDatabase 抛出 InvalidOperationException | [当前无测试覆盖] |
| AC-02.10 | 孤岛模式下 GetServer 返回 null | [当前无测试覆盖] |
| AC-02.11 | 孤岛模式下 Subscribe 为空操作 | [当前无测试覆盖] |

### AC-03: RedisConnectionProvider 连接重试

连接重试 / 节流 / 事件处理已移至 Core 的 `IConnectionMultiplexer` 注册，Engine 不再负责。
`AC-03`、`AC-04`、`AC-05` 已从 Engine 文档移除。

### AC-06: DI 注册

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-06.1 | 注册 `RedisConnectionProvider` 为 `IRedisConnectionProvider` Singleton（注入 Core 的 `IConnectionMultiplexer`） | `ServiceCollectionExtensions` |

### AC-08: 配置消费方 — ConfigProvider

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-08.1 | Redis 不可用时跳过 Redis 查找，进入孤岛模式 | `ConfigProviderTests` [间接覆盖] |
| AC-08.2 | Redis 可用时从 `agent:config:{agentId}` 加载配置 | `ConfigProviderTests.GetConfigAsync_loads_from_redis_when_snapshot_miss` |
| AC-08.3 | 从 `agent:published:index` 获取 Agent 列表 | [当前无测试覆盖] |

### AC-09: 配置热加载 — HotReloadService

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-09.1 | 订阅 `agent:config:updates` 频道 | `HotReloadTests` [间接覆盖] |
| AC-09.2 | 订阅所有 Legacy 频道 | `HotReloadTests.ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot` |
| AC-09.3 | 结构化消息触发全量刷新 | `HotReloadTests.ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis` |
| AC-09.4 | FullSync 清空快照 | `HotReloadTests.ProcessMessage_FullSync_ClearsSnapshot` |
| AC-09.5 | ConfigUpdate 类型触发从 Redis 全量刷新 | `HotReloadTests.ProcessMessage_ConfigUpdate_RefreshesFullConfigFromRedis` |
| AC-09.6 | 空白消息被忽略 | `HotReloadTests.ProcessMessage_IgnoresBlankPayload` |
| AC-09.7 | 无效 JSON 不覆盖已有 Snapshot | `HotReloadTests.ProcessMessage_InvalidJson_DoesNotOverwriteExistingSnapshot` |
| AC-09.8 | Legacy 注册频道不修改 Snapshot | `HotReloadTests.ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot` |

---

## 3. 非功能需求（NFR）

| 编号 | 需求 |
|------|------|
| NFR-01 | 薄包装不自管连接，线程安全由底层 `IConnectionMultiplexer` 保证 |
| NFR-02 | 连接失败不阻塞调用线程（连接为 null 时返回安全默认值） |
| NFR-03 | 重连 / 节流由 `ConnectionMultiplexer` 自动处理，Engine 不再负责 |
| NFR-04 | 连接配置（AbortOnConnectFail / ConnectTimeout）由 Agent.Core 负责 |

## Design


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

## Tasks


> 关联文档：[02-SPEC.md](./02-SPEC.md) | [03-DESIGN.md](./03-DESIGN.md) | [05-TESTS.md](./05-TESTS.md)

---

```json
[
  {
    "id": "T-01",
    "title": "定义 IRedisConnectionProvider 接口",
    "description": "定义 internal 接口，包含 IsAvailable、GetServer、GetDatabase、StringGetAsync、StringSetAsync、KeyDeleteAsync、SetMembersAsync、SetAddAsync、PingAsync、StringGet、Subscribe 方法，继承 IDisposable",
    "file": "src/Engine/Abstractions/IRedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-01"]
  },
  {
    "id": "T-02",
    "title": "实现 RedisConnectionProvider — 薄包装",
    "description": "注入 IConnectionMultiplexer?（Core 注册），连接为 null 时进入孤岛模式；IsAvailable 纯探测不触发重连",
    "file": "src/Engine/Redis/RedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-02.1", "FR-02.2", "FR-02.3"]
  },
  {
    "id": "T-05",
    "title": "实现 RedisConnectionProvider — 孤岛模式操作",
    "description": "所有数据操作在连接不可用时返回安全默认值：StringGetAsync→Null, StringSetAsync→false（When.Always）, KeyDeleteAsync→false, SetMembersAsync→空数组, SetAddAsync→false, PingAsync→Zero, StringGet→Null, GetDatabase→InvalidOperationException, GetServer→null, Subscribe→空操作",
    "file": "src/Engine/Redis/RedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-02.4", "FR-02.5", "FR-02.6", "FR-02.7", "FR-02.8", "FR-02.9", "FR-02.10", "FR-02.11", "FR-02.12", "FR-02.13"]
  },
  {
    "id": "T-06",
    "title": "RedisConnectionProvider Dispose 为空操作",
    "description": "IConnectionMultiplexer 生命周期由 DI 容器管理，Dispose 不再释放资源",
    "file": "src/Engine/Redis/RedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-02.14"]
  },
  {
    "id": "T-16",
    "title": "实现 DI 注册策略",
    "description": "在 ServiceCollectionExtensions.AddAgentEngine 中注册 RedisConnectionProvider 为 IRedisConnectionProvider Singleton。测试中通过 DI 注入 FakeRedisConnectionProvider 替代。",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs",
    "status": "implemented",
    "specRef": ["FR-04.1", "FR-04.2", "FR-04.3"]
  },
  {
    "id": "T-17",
    "title": "实现 RedisHealthCheck",
    "description": "实现 IHealthCheck：IsAvailable==false→Degraded, PingAsync 成功→Healthy, PingAsync 异常→Unhealthy",
    "file": "src/Engine/Redis/RedisHealthCheck.cs",
    "status": "implemented",
    "specRef": ["FR-05.1", "FR-05.2", "FR-05.3"]
  },
  {
    "id": "T-18",
    "title": "实现 RedisRegistry — Engine 注册与心跳",
    "description": "实现 IEngineRegistry：RegisterAsync/HeartbeatAsync 使用 StringSetAsync 写入 engine:registry:{id}，DeregisterAsync 使用 KeyDeleteAsync",
    "file": "src/Engine/Registry/RedisRegistry.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-19",
    "title": "实现 HeartbeatService — 后台心跳服务",
    "description": "BackgroundService：周期性调用 RegisterAsync/HeartbeatAsync，支持端口检测和重试",
    "file": "src/Engine/Redis/HeartbeatService.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-20",
    "title": "实现 ConfigProvider — Redis 配置加载",
    "description": "从 Redis 读取 agent:config:{id} 和 agent:published:index，支持 Snapshot 缓存和孤岛模式降级",
    "file": "src/Engine/Config/ConfigProvider.cs",
    "status": "implemented",
    "specRef": ["AC-08"]
  },
  {
    "id": "T-21",
    "title": "实现 HotReloadService — Pub/Sub 配置热加载",
    "description": "订阅 agent:config:updates 和 Legacy 频道，处理结构化/非结构化消息，更新 ConfigSnapshot",
    "file": "src/Engine/Reload/HotReloadService.cs",
    "status": "implemented",
    "specRef": ["AC-09"]
  },
  {
    "id": "T-22",
    "title": "实现 RedisSkillRegistrar — 技能注册",
    "description": "IHostedService：启动时从 skill:published:index 和 skill:registry:{name} 加载技能并注册到 IToolRegistry",
    "file": "src/Engine/Redis/RedisSkillRegistrar.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-23",
    "title": "实现 RedisLlmRegistrar — LLM 配置注册",
    "description": "IHostedService：启动时从 llm:published:index 和 llm:registry:{id} 加载 LLM 配置并注册到 ILlmRegistry",
    "file": "src/Engine/Redis/RedisLlmRegistrar.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-24",
    "title": "实现 RedisRagRegistrar — RAG 实例注册",
    "description": "IHostedService：启动时从 rag:published:index 和 rag:registry:{id} 加载 RAG 配置并注册到 IRagRegistry",
    "file": "src/Engine/Redis/RedisRagRegistrar.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-25",
    "title": "实现 FakeRedisConnectionProvider — 测试替身",
    "description": "内存 Dictionary 存储的 IRedisConnectionProvider 实现，用于单元测试",
    "file": "test/OpenAgent.Engine.Tests/TestDoubles/FakeRedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-26",
    "title": "编写 RedisHealthCheck 单元测试",
    "description": "测试 Degraded/Healthy/Unhealthy 三种状态",
    "file": "test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs",
    "status": "implemented",
    "specRef": ["AC-01"]
  },
  {
    "id": "T-27",
    "title": "编写 HotReloadService 单元测试",
    "description": "测试结构化消息、Legacy 消息、过期版本、空白消息、无效 JSON 等场景",
    "file": "test/OpenAgent.Engine.Tests/Config/HotReloadTests.cs",
    "status": "implemented",
    "specRef": ["AC-09"]
  },
  {
    "id": "T-28",
    "title": "编写 ConfigProvider 单元测试",
    "description": "测试 Snapshot 加载、Redis 加载、Mock 降级等场景",
    "file": "test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs",
    "status": "implemented",
    "specRef": ["AC-08"]
  }
]
```

## Tests


> 关联文档：[02-SPEC.md](./02-SPEC.md) | [04-TASKS.md](./04-TASKS.md)

---

## 1. 现有测试

### 1.1 RedisHealthCheckTests

**文件路径：** `test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs`

#### TC-HC-01: Redis 不可用时返回 Degraded

- **Given** FakeRedisConnectionProvider 的 `IsAvailableValue = false`
- **When** 调用 `RedisHealthCheck.CheckHealthAsync()`
- **Then** 返回 `HealthCheckResult`，`Status == HealthStatus.Degraded`

```xunit
[Fact] public async Task Returns_degraded_when_redis_not_available()
```

#### TC-HC-02: Ping 成功时返回 Healthy

- **Given** FakeRedisConnectionProvider 的 `IsAvailableValue = true`，`PingAsync` 正常返回
- **When** 调用 `RedisHealthCheck.CheckHealthAsync()`
- **Then** 返回 `HealthCheckResult`，`Status == HealthStatus.Healthy`

```xunit
[Fact] public async Task Returns_healthy_when_ping_succeeds()
```

#### TC-HC-03: Ping 抛异常时返回 Unhealthy

- **Given** FakeRedisConnectionProvider 的 `IsAvailableValue = true`，`PingException = RedisConnectionException`
- **When** 调用 `RedisHealthCheck.CheckHealthAsync()`
- **Then** 返回 `HealthCheckResult`，`Status == HealthStatus.Unhealthy`，`Exception != null`

```xunit
[Fact] public async Task Returns_unhealthy_when_ping_throws()
```

---

### 1.2 HotReloadTests

**文件路径：** `test/OpenAgent.Engine.Tests/Config/HotReloadTests.cs`

#### TC-HR-01: Legacy Agent 频道消息刷新 Snapshot

- **Given** FakeRedisConnectionProvider 中存储了 `agent:config:agent-a` 的 JSON 配置
- **When** 调用 `ProcessMessage("agent:config:changed", "agent-a")`
- **Then** Snapshot 中 `FullAgentConfig` 的 `Llm.Provider == "provider-a"`

```xunit
[Fact] public void ProcessMessage_RefreshesSnapshotFromLegacyAgentChannel()
```

#### TC-HR-02: 结构化消息从当前频道触发全量刷新

- **Given** 空的 ConfigSnapshot 和 FakeRedisConnectionProvider
- **When** 调用 `ProcessMessage("agent:config:updates", structuredJson)`，JSON 包含 `configType: "LLMSettings"`
- **Then** 忽略 payload 子配置 data，从 Redis 全量刷新，`LLMSettings` 的 `Provider == "provider-b"`

```xunit
[Fact] public void ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis()
```

#### TC-HR-04: ConfigUpdate 类型触发从 Redis 全量刷新

- **Given** FakeRedisConnectionProvider 中存储了 `agent:config:agent-d` 的 JSON 配置
- **When** 收到 `type: "ConfigUpdate"` 的结构化消息
- **Then** 从 Redis 读取完整配置并更新 Snapshot，版本号正确

```xunit
[Fact] public void ProcessMessage_ConfigUpdate_RefreshesFullConfigFromRedis()
```

#### TC-HR-05: FullSync 清空快照

- **Given** Snapshot 中已存在 agent-full-sync 的完整配置
- **When** 收到 `type: "FullSync"` 消息
- **Then** 整个快照被清空

```xunit
[Fact] public void ProcessMessage_FullSync_ClearsSnapshot()
```

#### TC-HR-06: 空白消息被忽略

- **Given** 空的 ConfigSnapshot
- **When** 调用 `ProcessMessage(channel, "   ")`
- **Then** Snapshot 不受影响

```xunit
[Fact] public void ProcessMessage_IgnoresBlankPayload()
```

#### TC-HR-07: 无效 JSON 不覆盖已有 Snapshot

- **Given** Snapshot 中已存在 `agent-e` 的 `LLMSettings`，版本号 5
- **When** 调用 `ProcessMessage(channel, "{ invalid json")`
- **Then** Snapshot 中配置不变，版本号仍为 5

```xunit
[Fact] public void ProcessMessage_InvalidJson_DoesNotOverwriteExistingSnapshot()
```

#### TC-HR-08: Legacy 注册频道不修改 Snapshot

- **Given** Snapshot 中已存在 `agent-f` 的 `LLMSettings`，版本号 6
- **When** 调用 `ProcessMessage("skill:registry:changed", "agent-f")`
- **Then** Snapshot 中配置不变，版本号仍为 6

```xunit
[Fact] public void ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot()
```

---

### 1.3 ConfigProviderTests

**文件路径：** `test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs`

#### TC-CP-01: 无 agentId 调用抛 InvalidOperationException

- **Given** ConfigProvider 实例
- **When** 调用 `GetConfigAsync(CancellationToken.None)`（无 agentId）
- **Then** 抛出 `InvalidOperationException`

```xunit
[Fact] public async Task GetConfigAsync_without_agentId_throws()
```

#### TC-CP-02: 空 agentId + AllowMock=true 返回 Mock 配置

- **Given** `AllowMockAgent = true`
- **When** 调用 `GetConfigAsync("")`
- **Then** 返回 `FrameworkType == Mock` 的配置

```xunit
[Fact] public async Task GetConfigAsync_with_empty_agentId_returns_mock_when_allowed()
```

#### TC-CP-03: 空 agentId + AllowMock=false 返回 null

- **Given** `AllowMockAgent = false`
- **When** 调用 `GetConfigAsync("")`
- **Then** 返回 `null`

```xunit
[Fact] public async Task GetConfigAsync_with_empty_agentId_returns_null_when_not_allowed()
```

#### TC-CP-04: 从 Snapshot 加载配置

- **Given** Snapshot 中已存在 `agent-snap` 的完整配置
- **When** 调用 `GetConfigAsync("agent-snap")`
- **Then** 返回 Snapshot 中的配置，不从 Redis 读取

```xunit
[Fact] public async Task GetConfigAsync_loads_from_snapshot()
```

#### TC-CP-05: Snapshot 未命中时从 Redis 加载

- **Given** FakeRedisConnectionProvider 中存储了 `agent:config:agent-redis` 的 JSON 配置，Snapshot 为空
- **When** 调用 `GetConfigAsync("agent-redis")`
- **Then** 返回 Redis 中的配置，Snapshot 被缓存，版本号正确

```xunit
[Fact] public async Task GetConfigAsync_loads_from_redis_when_snapshot_miss()
```

#### TC-CP-06: 无配置 + AllowMock=true 返回 Mock 降级

- **Given** FakeRedisConnectionProvider 中无对应配置，`AllowMockAgent = true`
- **When** 调用 `GetConfigAsync("nonexistent-agent")`
- **Then** 返回 `FrameworkType == Mock` 的配置

```xunit
[Fact] public async Task GetConfigAsync_returns_mock_fallback_when_nothing_found_and_allowed()
```

#### TC-CP-07: 无配置 + AllowMock=false 返回 null

- **Given** FakeRedisConnectionProvider 中无对应配置，`AllowMockAgent = false`
- **When** 调用 `GetConfigAsync("nonexistent-agent")`
- **Then** 返回 `null`

```xunit
[Fact] public async Task GetConfigAsync_returns_null_when_nothing_found_and_not_allowed()
```

---

## 2. 缺失测试场景

### 2.1 RedisConnectionProvider — 孤岛模式

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-01 | 无连接字符串进入孤岛模式 | **Given** IConfiguration 中无 `ConnectionStrings:Redis`；**When** 创建 RedisConnectionProvider；**Then** `IsAvailable == false`，不抛异常 |
| MT-02 | 孤岛模式 StringGetAsync 返回 Null | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `StringGetAsync("any:key")`；**Then** 返回 `RedisValue.Null` |
| MT-03 | 孤岛模式 StringSetAsync 返回 false | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `StringSetAsync("any:key", "value")`；**Then** 返回 `false` |
| MT-04 | 孤岛模式 KeyDeleteAsync 返回 false | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `KeyDeleteAsync("any:key")`；**Then** 返回 `false` |
| MT-05 | 孤岛模式 SetMembersAsync 返回空数组 | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `SetMembersAsync("any:key")`；**Then** 返回空数组 |
| MT-06 | 孤岛模式 SetAddAsync 返回 false | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `SetAddAsync("any:key", "value")`；**Then** 返回 `false` |
| MT-07 | 孤岛模式 PingAsync 返回 Zero | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `PingAsync()`；**Then** 返回 `TimeSpan.Zero` |
| MT-08 | 孤岛模式 StringGet 返回 Null | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `StringGet("any:key")`；**Then** 返回 `RedisValue.Null` |
| MT-09 | 孤岛模式 GetDatabase 抛异常 | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `GetDatabase()`；**Then** 抛出 `InvalidOperationException` |
| MT-10 | 孤岛模式 GetServer 返回 null | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `GetServer()`；**Then** 返回 `null` |
| MT-11 | 孤岛模式 Subscribe 为空操作 | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `Subscribe(channel, handler)`；**Then** 不抛异常 |

### 2.2 FakeRedisConnectionProvider（测试替身）

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-20 | FakeRedis 内存存储 | **Given** FakeRedisConnectionProvider 实例；**When** 调用 `StringSetAsync(key, value)`；**Then** 值存储在内存字典中 |
| MT-21 | FakeRedis 读取已存储的值 | **Given** 已存储的 key-value；**When** 调用 `StringGetAsync(key)`；**Then** 返回存储的值 |
| MT-22 | FakeRedis 不存在的 key | **Given** 空的 FakeRedisConnectionProvider；**When** 调用 `StringGetAsync(key)`；**Then** 返回 `RedisValue.Null` |

### 2.5 DI 注册

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-25 | Core 的 IConnectionMultiplexer 注册 RedisConnectionProvider | **Given** `ConnectionStrings:Redis = "localhost:6379"`；**When** 调用 `AddAgentCore` + `AddAgentEngine`；**Then** `IRedisConnectionProvider` 解析为 `RedisConnectionProvider` 实例 |
| MT-26 | 测试中使用 FakeRedisConnectionProvider | **Given** 测试 DI 容器；**When** 注册 `FakeRedisConnectionProvider` 为 `IRedisConnectionProvider`；**Then** 所有 Redis 操作使用内存 Mock |

### 2.7 ConfigProvider — Redis 不可用

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-27 | Redis 不可用时跳过 Redis 查找 | **Given** `IsAvailable == false`；**When** 调用 `GetConfigAsync("agent-x")`；**Then** 不尝试从 Redis 读取，返回 null 或 Mock 降级 |
| MT-28 | ListAgentsAsync 在 Redis 不可用时返回空列表 | **Given** `IsAvailable == false`；**When** 调用 `ListAgentsAsync()`；**Then** 返回空列表 |

### 2.8 RedisRegistry

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-29 | RegisterAsync 成功注册 | **Given** Redis 可用；**When** 调用 `RegisterAsync()`；**Then** `IsRegistered == true`，Redis 中存在 `engine:registry:{id}` 键 |
| MT-30 | RegisterAsync 失败时 IsRegistered 为 false | **Given** Redis 不可用（孤岛模式）；**When** 调用 `RegisterAsync()`；**Then** `IsRegistered == false`，不抛异常 |
| MT-31 | DeregisterAsync 删除注册键 | **Given** Engine 已注册；**When** 调用 `DeregisterAsync()`；**Then** `IsRegistered == false`，Redis 中键被删除 |

### 2.9 Registrar 系列

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-32 | RedisSkillRegistrar 在 Redis 不可用时跳过 | **Given** `IsAvailable == false`；**When** 调用 `StartAsync()`；**Then** 不尝试读取 Redis，直接返回 |
| MT-33 | RedisLlmRegistrar 在 Redis 不可用时跳过 | **Given** `IsAvailable == false`；**When** 调用 `StartAsync()`；**Then** 不尝试读取 Redis，直接返回 |
| MT-34 | RedisRagRegistrar 在 Redis 不可用时跳过 | **Given** `IsAvailable == false`；**When** 调用 `StartAsync()`；**Then** 不尝试读取 Redis，直接返回 |

---

## 3. 测试替身

### 3.1 FakeRedisConnectionProvider（TestDoubles）

**文件路径：** `test/OpenAgent.Engine.Tests/TestDoubles/FakeRedisConnectionProvider.cs`

- 基于 `Dictionary<string, string>` 的内存实现
- `IsAvailable` 固定返回 `true`
- `SetMembersAsync` / `SetAddAsync` 返回固定值（空数组 / true）
- `Subscribe` 为空操作
- 提供 `SetString(key, value)` 辅助方法用于测试设置

### 3.2 内联 Fake（RedisHealthCheckTests）

**文件路径：** `test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs`

- 私有 sealed 类，支持 `IsAvailableValue` 和 `PingException` 属性
- 用于精确控制健康检查的输入条件

### 3.3 内联 Fake（ConfigProviderTests）

**文件路径：** `test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs`

- 私有 sealed 类，支持 `SetString` 辅助方法
- `StringGetAsync` 从 Dictionary 读取

## Conventions


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
