# ServiceRegistration - 设计文档

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
