# ServiceRegistration - 测试文档

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
