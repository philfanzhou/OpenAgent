# GracefulShutdown - 测试文档

## 现有测试

当前无针对 GracefulShutdown 功能的专门测试文件。

## 缺失测试场景

### TC-GS-001: 正常请求注册

- **Given** Engine 正常运行（`IsShuttingDown == false`）
- **When** 调用 `RegisterRequest("Chat", "trace-001")`
- **Then** 返回 8 位字符串 requestId，`InFlightRequestCount == 1`

### TC-GS-002: 请求完成移除

- **Given** 有 1 个进行中请求
- **When** 调用 `CompleteRequest(requestId)`
- **Then** `InFlightRequestCount == 0`

### TC-GS-003: 完成不存在的请求

- **Given** 无进行中请求
- **When** 调用 `CompleteRequest("nonexistent")`
- **Then** 不抛出异常，`InFlightRequestCount == 0`

### TC-GS-004: 停机时拒绝新请求

- **Given** `IsShuttingDown == true`
- **When** 调用 `RegisterRequest("Chat")`
- **Then** 抛出 `AgentException`，ErrorCode 为 `DependencyUnavailable`，消息包含 "Engine is shutting down"

### TC-GS-005: 所有请求在超时内完成

- **Given** 有 2 个进行中请求，超时 30 秒
- **When** 两个请求在 5 秒内完成
- **Then** `ShutdownAsync` 正常返回，`InFlightRequestCount == 0`

### TC-GS-006: 超时后仍有请求

- **Given** 有 1 个进行中请求，超时 1 秒
- **When** 请求未在 1 秒内完成
- **Then** `ShutdownAsync` 在超时后返回，日志记录 Warning

### TC-GS-007: 无进行中请求时立即完成

- **Given** 无进行中请求
- **When** 调用 `ShutdownAsync(timeout)`
- **Then** 立即返回，日志记录 "Graceful shutdown completed successfully"

### TC-GS-008: RequestScope 自动注册与完成

- **Given** Engine 正常运行
- **When** 使用 `using var scope = new RequestScope(service, "Chat", "trace-001")`
- **Then** 构造时 `InFlightRequestCount` 增加，Dispose 时减少

### TC-GS-009: RequestScope 防重复释放

- **Given** 一个 RequestScope 已 Dispose
- **When** 再次调用 Dispose
- **Then** 不抛出异常，`InFlightRequestCount` 不再减少

### TC-GS-010: RequestScope 在停机时构造失败

- **Given** `IsShuttingDown == true`
- **When** 尝试创建 `new RequestScope(service, "Chat")`
- **Then** 抛出 `AgentException`

### TC-GS-011: 并发注册与完成

- **Given** Engine 正常运行
- **When** 多线程并发调用 RegisterRequest 和 CompleteRequest
- **Then** 不抛出异常，计数准确

### TC-GS-012: TraceId 默认值

- **Given** 调用 `RegisterRequest("Chat")` 未传 traceId
- **When** 注册成功
- **Then** InFlightRequest 的 TraceId 等于 requestId

### TC-GS-013: 停机顺序验证

- **Given** Engine 有进行中请求
- **When** 收到 ApplicationStopping 信号
- **Then** 先执行 ShutdownAsync，再执行 DeregisterAsync
