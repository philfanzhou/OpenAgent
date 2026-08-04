# ServiceRegistration - 功能规格说明

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
