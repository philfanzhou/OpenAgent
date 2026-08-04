# GracefulShutdown - 编码约定

## 命名约定

### 类命名

- 服务类后缀 `Service`：`ShutdownService`
- 作用域类后缀 `Scope`：`RequestScope`
- 内部数据类：`InFlightRequest`

### 方法命名

- 注册方法：`RegisterRequest`
- 完成方法：`CompleteRequest`
- 停机方法：`ShutdownAsync`
- 属性：`InFlightRequestCount`、`IsShuttingDown`

### 字段命名

- 私有字段使用 `_` 前缀 + camelCase：`_inFlightRequests`、`_shutdownSemaphore`、`_isShuttingDown`、`_logger`、`_service`、`_requestId`、`_disposed`
- `volatile` 字段同样使用 `_` 前缀：`_isShuttingDown`

## 日志约定

### 日志级别

| 场景 | 级别 | 示例 |
|------|------|------|
| 停机开始 | Information | `"Initiating graceful shutdown with timeout: {Timeout}s"` |
| 等待请求 | Information | `"Waiting for {RemainingRequests} in-flight requests to complete..."` |
| 停机完成 | Information | `"Graceful shutdown completed successfully. All requests finished in {DurationMs}ms"` |
| 注册请求 | Debug | `"Registered in-flight request: {RequestId}, Type: {RequestType}, TraceId: {TraceId}"` |
| 完成请求 | Debug | `"Completed in-flight request: {RequestId}, Duration: {DurationMs}ms"` |
| 等待中的请求 | Debug | `"Pending request: {RequestId}, Type: {RequestType}, Duration: {DurationMs}ms"` |
| 超时仍有请求 | Warning | `"Shutdown timeout reached. {RemainingRequests} requests are still running and may be terminated by the host"` |
| 超时仍在运行的请求 | Warning | `"Request still running at shutdown timeout: {RequestId}, Type: {RequestType}, Duration: {DurationMs}ms"` |

### 结构化日志参数

- `{Timeout}`、`{RemainingRequests}`、`{RequestId}`、`{RequestType}`、`{TraceId}`、`{DurationMs}`

### 日志消息风格

- 使用完整英文句子
- 超时消息明确说明后果："may be terminated by the host"

## 错误处理约定

### 异常类型

- 停机时拒绝新请求：`AgentException(AgentErrorCode.DependencyUnavailable, "Engine is shutting down, no new requests accepted")`
- 使用合约包定义的 `AgentException` 和 `AgentErrorCode`

### 异常消息格式

- `"Engine is shutting down, no new requests accepted"` — 明确说明原因和后果

## 并发约定

- 使用 `ConcurrentDictionary<string, InFlightRequest>` 存储进行中请求
- `_isShuttingDown` 使用 `volatile` 确保多线程可见性
- `ShutdownAsync` 中的轮询不使用锁，仅读取 `Count` 属性
- `_shutdownSemaphore` 使用 `SemaphoreSlim` 而非 `ManualResetEvent`，支持异步等待

## DI 注册约定

- Singleton + HostedService 双重注册：
  ```csharp
  services.AddSingleton<ShutdownService>();
  services.AddHostedService(sp => sp.GetRequiredService<ShutdownService>());
  ```
- 使用 `sp.GetRequiredService<ShutdownService>()` 确保 HostedService 和 Singleton 是同一实例

## 停机编排约定

- 使用 `IHostApplicationLifetime.ApplicationStopping` 注册停机回调
- 同步等待：`.GetAwaiter().GetResult()`
- 停机顺序：先 `ShutdownService.ShutdownAsync` → 再 `RedisRegistry.DeregisterAsync`
- 超时从配置读取：`builder.Configuration.GetValue("Shutdown:TimeoutSeconds", 30)`

## 请求 ID 生成约定

- 格式：`Guid.NewGuid().ToString("N")[..8]`
- 与 ServiceRegistration 的 EngineId 生成方式一致
- "N" 格式为 32 位无连字符十六进制，取前 8 位

## 时间计算约定

- 使用 `DateTime.UtcNow` 记录开始时间
- 使用 `Stopwatch` 测量停机等待时间
- 持续时间以毫秒为单位记录：`duration.TotalMilliseconds`
