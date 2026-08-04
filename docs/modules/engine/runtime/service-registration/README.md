
## Feature


## 核心用户故事

作为 Engine 运维人员，我希望 Engine 实例能自动在 Redis 中注册、发送心跳并在停机时注销，以便 Router 能够发现和路由流量到活跃的 Engine 节点。

## 功能简介

ServiceRegistration 负责 Engine 实例在分布式环境中的自动注册与发现。每个 Engine 实例启动后，会生成唯一 EngineId，将自身信息（主机、端口、负载）写入 Redis，并周期性发送心跳维持注册状态。停机时主动从 Redis 注销，确保 Router 仅将流量路由到活跃节点。

## 关键能力

- **自动注册**：应用启动后自动在 Redis 中注册 Engine 实例信息
- **心跳维持**：周期性更新注册信息（含负载指标），刷新 TTL
- **主动注销**：停机时从 Redis 删除注册信息
- **孤岛模式**：Redis 不可用时降级运行，不阻塞 Engine 启动
- **负载上报**：综合内存、GC、线程池压力计算负载值
 - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定

## Specification


## 功能需求 (FR)

### FR-SR-001: Engine 注册

- **方法签名**: `Task RegisterAsync(CancellationToken cancellationToken = default)`
- **行为**: 将 RegistryEntry 序列化为 JSON，通过 `StringSetAsync` 写入 Redis key `engine:registry:{engineId}`，TTL 由 `RegistryTtlSeconds` 控制
- **EngineId 生成**: `Guid.NewGuid().ToString("N")[..8]`，取 GUID 前 8 位
- **Host 解析**: 构造时若 `AdvertisedHost` 为空则使用 `Dns.GetHostName()`；HeartbeatService 在 ApplicationStarted 回调中覆盖为 `Environment.MachineName`（当 AdvertisedHost 为空时）
- **Port 解析**: 构造时若 `AdvertisedPort` 有值则使用配置值，否则默认 0；HeartbeatService 在 ApplicationStarted 回调中通过 `DetectPort()` 检测并覆盖
- **失败处理**: 捕获异常后进入孤岛模式（`IsRegistered = false`），记录 Warning 日志 "Continuing in island mode"

### FR-SR-002: 心跳维持

- **方法签名**: `Task HeartbeatAsync(CancellationToken cancellationToken = default)`
- **行为**: 更新 `LastHeartbeat` 为 `DateTime.UtcNow`，重新计算 `Load`，序列化后 `StringSetAsync` 刷新 TTL
- **前置条件**: `IsRegistered == true`，否则直接返回
- **失败处理**: 捕获异常后设置 `IsRegistered = false`

### FR-SR-003: 主动注销

- **方法签名**: `Task DeregisterAsync(CancellationToken cancellationToken = default)`
- **行为**: 调用 `KeyDeleteAsync` 删除 Redis key，设置 `IsRegistered = false`
- **失败处理**: 捕获异常，记录 Warning 日志 "TTL will expire naturally"（依赖 TTL 自然过期）

### FR-SR-004: 负载计算

- **方法**: `GetCurrentLoad()` 静态方法
- **公式**: `memoryPressure * 0.4 + gcPressure * 0.3 + threadPoolPressure * 0.3`
- **结果范围**: `[0, 100]`，异常时返回 50
- **内存压力**: `GC.GetTotalMemory(false) * 100 / GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
- **GC 压力**: `(gen0 + gen1*2 + gen2*4) / 10`，上限 100
- **线程池压力**: `max(workerUtilization, ioUtilization)`

### FR-SR-005: 心跳后台服务

- **类型**: `BackgroundService`
- **启动流程**: 等待 `ApplicationStarted` 事件 → 检测端口 → 设置 Host/Port → 异步注册（`Task.Run`）
- **循环逻辑**:
  - `IsRegistered == false` → 调用 `RegisterAsync`
  - `IsRegistered == true` → 调用 `HeartbeatAsync`
- **正常间隔**: `IntervalSeconds`（默认 10s，最小 1s）
- **重试间隔**: `RetryDelaySeconds`（默认 5s，最小 1s）
- **端口检测**: 优先 `ASPNETCORE_HTTP_PORTS` → `ASPNETCORE_URLS` → 默认 80

### FR-SR-006: 停机注销

- **触发**: `IHostApplicationLifetime.ApplicationStopping`
- **流程**: 先 `ShutdownService.ShutdownAsync` → 再 `RedisRegistry.DeregisterAsync`

## 验收标准 (AC)

### AC-SR-001: 注册成功 [当前无测试覆盖]

- Given Redis 可用
- When 调用 `RegisterAsync`
- Then Redis key `engine:registry:{engineId}` 存在，值为 RegistryEntry JSON，TTL 为 RegistryTtlSeconds

### AC-SR-002: 注册失败进入孤岛模式 [当前无测试覆盖]

- Given Redis 不可用
- When 调用 `RegisterAsync`
- Then `IsRegistered == false`，日志记录 Warning

### AC-SR-003: 心跳更新负载和 TTL [当前无测试覆盖]

- Given Engine 已注册
- When 调用 `HeartbeatAsync`
- Then Redis 中的 RegistryEntry 更新了 LastHeartbeat、Load，TTL 被刷新

### AC-SR-004: 心跳失败标记未注册 [当前无测试覆盖]

- Given Engine 已注册但 Redis 连接中断
- When 调用 `HeartbeatAsync`
- Then `IsRegistered == false`

### AC-SR-005: 注销删除 Redis key [当前无测试覆盖]

- Given Engine 已注册
- When 调用 `DeregisterAsync`
- Then Redis key 被删除，`IsRegistered == false`

### AC-SR-006: 注销失败依赖 TTL [当前无测试覆盖]

- Given Engine 已注册但 Redis 不可用
- When 调用 `DeregisterAsync`
- Then 日志记录 Warning "TTL will expire naturally"

### AC-SR-007: 负载计算范围 [当前无测试覆盖]

- Given 任意系统状态
- When 调用 `GetCurrentLoad()`
- Then 返回值在 `[0, 100]` 范围内

### AC-SR-008: 端口检测优先级 [当前无测试覆盖]

- Given 环境变量 `ASPNETCORE_HTTP_PORTS=5000`
- When HeartbeatService 检测端口
- Then 使用 5000

### AC-SR-009: 停机时先等待请求再注销 [当前无测试覆盖]

- Given Engine 有进行中的请求
- When 收到停机信号
- Then 先等待请求完成（超时 Shutdown:TimeoutSeconds），再从 Redis 注销

## 配置项

| 配置路径 | 默认值 | 说明 |
|---------|--------|------|
| Heartbeat:IntervalSeconds | 10 | 心跳间隔（秒） |
| Heartbeat:RetryDelaySeconds | 5 | 重试延迟（秒） |
| Heartbeat:RegistryTtlSeconds | 30 | 注册 TTL（秒） |
| Heartbeat:AdvertisedHost | null | 对外宣告的主机名 |
| Heartbeat:AdvertisedPort | null | 对外宣告的端口 |

## Design


## 架构概览

```
┌─────────────┐     RegisterAsync/HeartbeatAsync/DeregisterAsync     ┌─────────┐
│ HeartbeatService │ ──────────────────────────────────────────────→ │  Redis  │
│ (BackgroundService) │                                              │         │
└─────────────┘                                                        └─────────┘
       │                                                                    ↑
       │ uses                                                               │
       ↓                                                                    │
┌─────────────┐                                                             │
│ RedisRegistry │ ─────────── StringSetAsync / KeyDeleteAsync ──────────────→ │
│ (IEngineRegistry) │                                                       │
└─────────────┘
       │
       │ holds
       ↓
┌─────────────┐
│ RegistryEntry │
└─────────────┘
```

## 文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/Engine/Abstractions/IEngineRegistry.cs` | 注册接口定义 |
| `src/Engine/Registry/RedisRegistry.cs` | Redis 实现类 |
| `src/Engine/Redis/HeartbeatService.cs` | 心跳后台服务 |
| `src/Engine/Models/HeartbeatOptions.cs` | 心跳配置模型 |
| `src/Engine/Models/RegistryEntry.cs` | 注册条目数据模型 |
| `src/Engine/Extensions/ServiceCollectionExtensions.cs` | DI 注册 |
| `src/Host/Program.cs` | 停机注销编排 |

## 接口定义

### IEngineRegistry

```csharp
public interface IEngineRegistry
{
    Task RegisterAsync(CancellationToken cancellationToken = default);
    Task HeartbeatAsync(CancellationToken cancellationToken = default);
    Task DeregisterAsync(CancellationToken cancellationToken = default);
    bool IsRegistered { get; }
}
```

### HeartbeatOptions

```csharp
internal class HeartbeatOptions
{
    public int IntervalSeconds { get; set; } = 10;
    public int RetryDelaySeconds { get; set; } = 5;
    public int RegistryTtlSeconds { get; set; } = 30;
    public string? AdvertisedHost { get; set; }
    public int? AdvertisedPort { get; set; }
}
```

### RegistryEntry

```csharp
internal class RegistryEntry
{
    public string EngineId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int Load { get; set; }
    public DateTime LastHeartbeat { get; set; }
}
```

## 数据依赖

### Redis 数据结构

| Key 模式 | 类型 | 值 | TTL |
|---------|------|-----|-----|
| `engine:registry:{engineId}` | String | RegistryEntry JSON | RegistryTtlSeconds (默认 30s) |

### RegistryEntry JSON 示例

```json
{
  "EngineId": "a1b2c3d4",
  "Host": "engine-node-01",
  "Port": 8080,
  "Load": 35,
  "LastHeartbeat": "2026-06-10T06:30:00Z"
}
```

## DI 注册

```csharp
// ServiceCollectionExtensions.cs
services.AddSingleton<IEngineRegistry, RedisRegistry>();
services.AddHostedService<HeartbeatService>();
```

## 启动与停机流程

### 启动流程

1. `HeartbeatService` 构造函数注册 `ApplicationStarted` 回调
2. `ExecuteAsync` 循环等待 `_portSet == true`
3. `ApplicationStarted` 触发后：
   - 检测端口（`ASPNETCORE_HTTP_PORTS` → `ASPNETCORE_URLS` → 80）
   - 设置 Host（`AdvertisedHost` → `Environment.MachineName`）
   - 设置 Port
   - 通过 `Task.Run` 异步调用 `RegisterAsync`
4. 进入心跳循环：未注册→注册，已注册→心跳

### 停机流程

1. `IHostApplicationLifetime.ApplicationStopping` 触发
2. 调用 `ShutdownService.ShutdownAsync(shutdownTimeout)` 等待进行中请求
3. 调用 `RedisRegistry.DeregisterAsync()` 从 Redis 注销

## 负载计算算法

```
Load = clamp(
    memoryPressure * 0.4 + gcPressure * 0.3 + threadPoolPressure * 0.3,
    0, 100
)
```

- **memoryPressure**: `GC.GetTotalMemory(false) * 100 / TotalAvailableMemoryBytes`
- **gcPressure**: `(gen0Count + gen1Count*2 + gen2Count*4) / 10`
- **threadPoolPressure**: `max(workerUtilization%, ioUtilization%)`
- 异常时返回 50

## 关键设计决策

1. **EngineId 使用 GUID 前 8 位**：足够在集群内唯一，同时保持 key 简短
2. **TTL 机制**：即使 Engine 崩溃未注销，TTL 过期后注册信息自动消失
3. **孤岛模式**：Redis 不可用时不阻塞启动，Engine 可独立运行
4. **端口延迟检测**：等待 `ApplicationStarted` 后才检测端口，确保 ASP.NET Core 已绑定
5. **IOptionsMonitor**：使用 `IOptionsMonitor<HeartbeatOptions>` 而非 `IOptions`，支持配置热更新

## Tasks


```json
[
  {
    "id": "SR-001",
    "title": "定义 IEngineRegistry 接口",
    "description": "定义 RegisterAsync、HeartbeatAsync、DeregisterAsync、IsRegistered 接口成员",
    "status": "implemented",
    "file": "src/Engine/Abstractions/IEngineRegistry.cs"
  },
  {
    "id": "SR-002",
    "title": "实现 RedisRegistry 注册逻辑",
    "description": "生成 EngineId，序列化 RegistryEntry，StringSetAsync 写入 Redis 并设置 TTL",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-003",
    "title": "实现 RedisRegistry 心跳逻辑",
    "description": "更新 LastHeartbeat 和 Load，刷新 TTL；失败时标记 IsRegistered=false",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-004",
    "title": "实现 RedisRegistry 注销逻辑",
    "description": "KeyDeleteAsync 删除注册 key；失败时记录 Warning 日志",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-005",
    "title": "实现负载计算算法",
    "description": "内存压力 40% + GC 压力 30% + 线程池压力 30%，结果 clamp 到 [0,100]",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-006",
    "title": "实现 HeartbeatService 后台服务",
    "description": "BackgroundService：等待端口检测 → 注册 → 心跳循环，含重试逻辑",
    "status": "implemented",
    "file": "src/Engine/Redis/HeartbeatService.cs"
  },
  {
    "id": "SR-007",
    "title": "实现端口自动检测",
    "description": "优先读取 ASPNETCORE_HTTP_PORTS，其次 ASPNETCORE_URLS，默认 80",
    "status": "implemented",
    "file": "src/Engine/Redis/HeartbeatService.cs"
  },
  {
    "id": "SR-008",
    "title": "定义 HeartbeatOptions 配置模型",
    "description": "IntervalSeconds=10, RetryDelaySeconds=5, RegistryTtlSeconds=30, AdvertisedHost, AdvertisedPort",
    "status": "implemented",
    "file": "src/Engine/Models/HeartbeatOptions.cs"
  },
  {
    "id": "SR-009",
    "title": "定义 RegistryEntry 数据模型",
    "description": "EngineId, Host, Port, Load, LastHeartbeat",
    "status": "implemented",
    "file": "src/Engine/Models/RegistryEntry.cs"
  },
  {
    "id": "SR-010",
    "title": "DI 注册与停机编排",
    "description": "ServiceCollectionExtensions 注册 IEngineRegistry/HeartbeatService；Program.cs 停机时先 ShutdownService 再 DeregisterAsync",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs, src/Host/Program.cs"
  },
  {
    "id": "SR-011",
    "title": "编写 ServiceRegistration 单元测试",
    "description": "覆盖注册成功/失败、心跳更新、注销、负载计算、端口检测等场景",
    "status": "pending",
    "file": ""
  }
]
```

## Tests


## 现有测试

当前无针对 ServiceRegistration 功能的专门测试文件。

## 缺失测试场景

### TC-SR-001: 注册成功

- **Given** Redis 可用且连接正常
- **When** 调用 `RedisRegistry.RegisterAsync()`
- **Then** Redis 中存在 key `engine:registry:{engineId}`，值为 RegistryEntry JSON，TTL 为 RegistryTtlSeconds，`IsRegistered == true`

### TC-SR-002: 注册失败进入孤岛模式

- **Given** Redis 不可用（`StringSetAsync` 抛出异常）
- **When** 调用 `RedisRegistry.RegisterAsync()`
- **Then** `IsRegistered == false`，日志记录 Warning "Continuing in island mode"

### TC-SR-003: 心跳更新负载和 TTL

- **Given** Engine 已注册（`IsRegistered == true`）
- **When** 调用 `RedisRegistry.HeartbeatAsync()`
- **Then** Redis 中的值更新了 `LastHeartbeat` 和 `Load`，TTL 被刷新

### TC-SR-004: 心跳失败标记未注册

- **Given** Engine 已注册但 Redis `StringSetAsync` 抛出异常
- **When** 调用 `RedisRegistry.HeartbeatAsync()`
- **Then** `IsRegistered == false`

### TC-SR-005: 未注册时心跳直接返回

- **Given** `IsRegistered == false`
- **When** 调用 `RedisRegistry.HeartbeatAsync()`
- **Then** 不调用 Redis，直接返回

### TC-SR-006: 注销成功删除 key

- **Given** Engine 已注册
- **When** 调用 `RedisRegistry.DeregisterAsync()`
- **Then** Redis key 被删除，`IsRegistered == false`

### TC-SR-007: 注销失败记录 Warning

- **Given** Engine 已注册但 Redis `KeyDeleteAsync` 抛出异常
- **When** 调用 `RedisRegistry.DeregisterAsync()`
- **Then** 日志记录 Warning "TTL will expire naturally"

### TC-SR-008: 负载计算结果范围

- **Given** 任意系统状态
- **When** 调用 `GetCurrentLoad()`
- **Then** 返回值在 `[0, 100]` 范围内

### TC-SR-009: 负载计算异常降级

- **Given** 内存/GC/线程池信息获取抛出异常
- **When** 调用 `GetCurrentLoad()`
- **Then** 返回 50

### TC-SR-010: 端口检测 - ASPNETCORE_HTTP_PORTS 优先

- **Given** 环境变量 `ASPNETCORE_HTTP_PORTS=5000;5001`
- **When** 调用 `HeartbeatService.DetectPort()`
- **Then** 返回 5000（取第一个）

### TC-SR-011: 端口检测 - ASPNETCORE_URLS 回退

- **Given** `ASPNETCORE_HTTP_PORTS` 未设置，`ASPNETCORE_URLS=http://0.0.0.0:8080`
- **When** 调用 `HeartbeatService.DetectPort()`
- **Then** 返回 8080

### TC-SR-012: 端口检测 - 默认值

- **Given** 两个环境变量均未设置
- **When** 调用 `HeartbeatService.DetectPort()`
- **Then** 返回 80

### TC-SR-013: AdvertisedPort 覆盖自动检测

- **Given** `HeartbeatOptions.AdvertisedPort = 9090`
- **When** HeartbeatService 设置端口
- **Then** 使用 9090 而非自动检测值

### TC-SR-014: HeartbeatService 循环逻辑

- **Given** HeartbeatService 已启动且端口已检测
- **When** `IsRegistered == false`
- **Then** 调用 `RegisterAsync`，间隔 `IntervalSeconds`

### TC-SR-015: HeartbeatService 重试逻辑

- **Given** 心跳循环中抛出非取消异常
- **When** 异常被捕获
- **Then** 延迟 `RetryDelaySeconds` 后重试

### TC-SR-016: 停机时先等待请求再注销

- **Given** Engine 有进行中的请求
- **When** 收到 `ApplicationStopping` 信号
- **Then** 先执行 `ShutdownService.ShutdownAsync`，再执行 `RedisRegistry.DeregisterAsync`

## 测试基础设施需求

- 需要实现 `FakeRedisConnectionProvider`，支持 `StringSetAsync`/`StringGetAsync`/`KeyDeleteAsync` 的内存模拟
- 需要能够模拟 `IOptionsMonitor<HeartbeatOptions>` 的行为
- 需要能够验证日志输出（`ILogger` mock）

## Conventions


## 命名约定

### 接口命名

- 注册接口前缀 `I`：`IEngineRegistry`
- 方法使用 `Async` 后缀：`RegisterAsync`、`HeartbeatAsync`、`DeregisterAsync`
- 布尔属性使用 `Is` 前缀：`IsRegistered`

### 类命名

- Redis 实现类前缀 `Redis`：`RedisRegistry`
- 后台服务后缀 `Service`：`HeartbeatService`
- 配置模型后缀 `Options`：`HeartbeatOptions`
- 数据模型后缀 `Entry`：`RegistryEntry`

### 方法命名

- 内部辅助方法使用 `PascalCase`：`GetCurrentLoad`、`GetMemoryPressure`、`GetGCPressure`、`GetThreadPoolPressure`
- 静态工厂/检测方法：`DetectPort`
- TTL 获取方法：`GetTtl`

### 字段命名

- 私有字段使用 `_` 前缀 + camelCase：`_redis`、`_entry`、`_logger`、`_options`、`_isRegistered`、`_disposed`、`_portSet`
- 静态只读字段使用 `PascalCase`：无此场景

## 日志约定

### 日志级别

| 场景 | 级别 | 示例 |
|------|------|------|
| 注册成功 | Information | `"Engine registered with ID: {EngineId} at {Host}:{Port}"` |
| 注销成功 | Information | `"Engine deregistered from Redis. ID: {EngineId}"` |
| 心跳服务启动 | Information | `"Engine heartbeat service starting..."` |
| 心跳服务停止 | Information | `"Engine heartbeat service stopped."` |
| 端口检测完成 | Information | `"Detected listening port after app start: {Port}"` |
| 注册失败 | Warning | `"Failed to register engine in Redis. Continuing in island mode."` |
| 心跳失败 | Warning | `"Failed to send heartbeat to Redis."` |
| 注销失败 | Warning | `"Failed to deregister engine from Redis. TTL will expire naturally."` |
| StringSetAsync 返回 false | Warning | `"Failed to register engine in Redis. StringSetAsync returned false."` |
| 心跳循环异常 | Warning | `"Heartbeat failed, will retry..."` |
| 未注册尝试心跳 | Information | `"Engine not registered. Attempting to register..."` |

### 结构化日志参数

- 使用命名参数：`{EngineId}`、`{Host}`、`{Port}`
- 日志消息为完整英文句子，首字母大写

## 错误处理约定

### 异常处理策略

- **不向上抛出**：`RegisterAsync`、`HeartbeatAsync`、`DeregisterAsync` 均捕获所有异常
- **降级而非崩溃**：Redis 不可用时进入孤岛模式，不阻止 Engine 运行
- **TTL 兜底**：注销失败时依赖 TTL 自然过期，不重试

### 异常消息格式

- `"Failed to register engine in Redis. Continuing in island mode."`
- `"Failed to send heartbeat to Redis."`
- `"Failed to deregister engine from Redis. TTL will expire naturally."`

## Redis Key 命名约定

- 格式：`{领域}:{操作}:{标识符}`
- 示例：`engine:registry:{engineId}`
- 使用冒号 `:` 作为分隔符

## DI 注册约定

- 接口注册为 Singleton：`services.AddSingleton<IEngineRegistry, RedisRegistry>()`
- 后台服务使用 `AddHostedService`：`services.AddHostedService<HeartbeatService>()`
- 配置使用 `Configure<T>`：`services.Configure<HeartbeatOptions>(configuration.GetSection("Heartbeat"))`

## 配置约定

- 配置节名称与 Options 类名对应（去掉 Options 后缀）：`Heartbeat` → `HeartbeatOptions`
- 默认值在 Options 类属性初始化器中设置
- TTL 最小值保护：`Math.Max(value, 1)`
- 间隔最小值保护：`Math.Max(value, 1)`

## 并发约定

- `IsRegistered` 使用普通 `bool`（非 volatile），因为心跳循环为单线程顺序执行
- `_portSet` 使用普通 `bool`，由 `ApplicationStarted` 回调设置后由主循环读取
