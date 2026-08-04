# GracefulShutdown - 功能规格说明

## 功能需求 (FR)

### FR-GS-001: 请求注册

- **方法签名**: `string RegisterRequest(string requestType, string? traceId = null)`
- **行为**:
  - 若 `_isShuttingDown == true`，抛出 `AgentException(AgentErrorCode.DependencyUnavailable, "Engine is shutting down, no new requests accepted")`
  - 生成 8 位 GUID 作为 requestId：`Guid.NewGuid().ToString("N")[..8]`
  - 创建 `InFlightRequest` 并加入 `_inFlightRequests` ConcurrentDictionary
  - 返回 requestId

### FR-GS-002: 请求完成

- **方法签名**: `void CompleteRequest(string requestId)`
- **行为**: 从 `_inFlightRequests` 中移除请求，计算并记录持续时间

### FR-GS-003: 优雅停机

- **方法签名**: `Task ShutdownAsync(TimeSpan timeout)`
- **行为**:
  1. 设置 `_isShuttingDown = true`
  2. 启动 Stopwatch 计时
  3. 循环等待：当 `_inFlightRequests.Count > 0` 且未超时时，每秒轮询一次
  4. 轮询期间记录剩余请求数和每个请求详情
  5. 超时后记录 Warning 日志，列出仍在运行的请求
  6. 全部完成后记录 Information 日志
  7. 释放 `_shutdownSemaphore`

### FR-GS-004: RequestScope

- **类型**: `IDisposable`
- **构造函数**: `RequestScope(ShutdownService service, string requestType, string? traceId = null)`
- **行为**: 构造时调用 `RegisterRequest`，Dispose 时调用 `CompleteRequest`
- **防重复释放**: 使用 `_disposed` 标志

### FR-GS-005: 后台服务生命周期

- **类型**: `BackgroundService`
- **行为**: `ExecuteAsync` 等待 `_shutdownSemaphore`，收到取消信号时退出

### FR-GS-006: 停机编排

- **触发**: `IHostApplicationLifetime.ApplicationStopping`
- **流程**: `ShutdownService.ShutdownAsync(shutdownTimeout)` → `RedisRegistry.DeregisterAsync()`
- **超时配置**: `Shutdown:TimeoutSeconds`（默认 30 秒）

## 验收标准 (AC)

### AC-GS-001: 正常请求注册与完成 [当前无测试覆盖]

- **Given** Engine 正常运行
- **When** 调用 `RegisterRequest("Chat", "trace-001")`
- **Then** 返回 8 位 requestId，`InFlightRequestCount` 增加 1

### AC-GS-002: 停机时拒绝新请求 [当前无测试覆盖]

- **Given** `_isShuttingDown == true`
- **When** 调用 `RegisterRequest("Chat")`
- **Then** 抛出 `AgentException`，ErrorCode 为 `DependencyUnavailable`，消息为 "Engine is shutting down, no new requests accepted"

### AC-GS-003: 请求完成后计数减少 [当前无测试覆盖]

- **Given** 有 1 个进行中请求
- **When** 调用 `CompleteRequest(requestId)`
- **Then** `InFlightRequestCount` 减少 1

### AC-GS-004: 所有请求在超时内完成 [当前无测试覆盖]

- **Given** 有 2 个进行中请求，超时 30 秒
- **When** 两个请求在 5 秒内完成
- **Then** `ShutdownAsync` 在约 5 秒后返回，日志记录 "Graceful shutdown completed successfully"

### AC-GS-005: 超时后仍有请求运行 [当前无测试覆盖]

- **Given** 有 1 个进行中请求，超时 5 秒
- **When** 请求未在 5 秒内完成
- **Then** 日志记录 Warning "Shutdown timeout reached"，列出仍在运行的请求

### AC-GS-006: RequestScope 自动注册与完成 [当前无测试覆盖]

- **Given** Engine 正常运行
- **When** 使用 `using var scope = new RequestScope(service, "Chat")`
- **Then** 构造时自动注册，Dispose 时自动完成

### AC-GS-007: 停机时先等待请求再注销 [当前无测试覆盖]

- **Given** Engine 有进行中请求
- **When** 收到 `ApplicationStopping` 信号
- **Then** 先执行 `ShutdownService.ShutdownAsync`，再执行 `RedisRegistry.DeregisterAsync`

## 配置项

| 配置路径 | 默认值 | 说明 |
|---------|--------|------|
| Shutdown:TimeoutSeconds | 30 | 停机等待超时（秒） |
