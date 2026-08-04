
## Feature


## 核心用户故事

作为 Engine 运维人员，我希望 Engine 在停机时能等待进行中的请求完成后再注销，以便部署期间不会丢失正在处理的请求。

## 功能简介

GracefulShutdown 通过跟踪进行中的请求，确保 Engine 停机时不会中断正在处理的请求。ShutdownService 维护一个进行中请求的并发字典，停机时设置关闭标志拒绝新请求，并等待所有进行中请求完成或超时。RequestScope 提供 IDisposable 包装，自动注册和完成请求。

## 关键能力

- **请求跟踪**：ConcurrentDictionary 跟踪所有进行中的请求
- **拒绝新请求**：停机时新请求抛出 AgentException(DependencyUnavailable)
- **等待完成**：停机时轮询等待进行中请求完成
- **超时保护**：超时后记录 Warning 日志，不强制终止
- **RequestScope**：IDisposable 包装，自动注册/完成请求
 - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
- [../ServiceRegistration/01-FEATURE.md](../ServiceRegistration/01-FEATURE.md) - 服务注册（停机注销顺序）

## Specification


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

## Design


## 架构概览

```
┌──────────────┐  RegisterRequest   ┌──────────────────┐
│  Controller   │ ────────────────→ │  ShutdownService  │
│  (请求入口)   │                    │  (BackgroundService)│
└──────────────┘                    └──────────────────┘
       │                                   │
       │ using RequestScope                │ _inFlightRequests
       ↓                                   ↓
┌──────────────┐                    ┌──────────────────┐
│ RequestScope  │  CompleteRequest  │ ConcurrentDict    │
│ (IDisposable) │ ────────────────→ │ <string, InFlight>│
└──────────────┘                    └──────────────────┘

停机流程:
ApplicationStopping → ShutdownService.ShutdownAsync(timeout) → RedisRegistry.DeregisterAsync()
```

## 文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/Engine/Services/ShutdownService.cs` | 停机服务（请求跟踪、等待完成） |
| `src/Engine/Services/RequestScope.cs` | 请求作用域（IDisposable 包装） |
| `src/Host/Program.cs` | 停机编排（ApplicationStopping 注册） |
| `src/Engine/Extensions/ServiceCollectionExtensions.cs` | DI 注册 |

## 类定义

### ShutdownService

```csharp
internal class ShutdownService : BackgroundService
{
    private readonly ConcurrentDictionary<string, InFlightRequest> _inFlightRequests = new();
    private readonly SemaphoreSlim _shutdownSemaphore = new(0, 1);
    private volatile bool _isShuttingDown = false;

    // 公开方法
    internal string RegisterRequest(string requestType, string? traceId = null);
    internal void CompleteRequest(string requestId);
    internal async Task ShutdownAsync(TimeSpan timeout);
    internal int InFlightRequestCount { get; }
    internal bool IsShuttingDown { get; }
}
```

### InFlightRequest（内部类）

```csharp
private class InFlightRequest
{
    public string Id { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
}
```

### RequestScope

```csharp
internal class RequestScope : IDisposable
{
    private readonly ShutdownService _service;
    private readonly string _requestId;
    private bool _disposed;

    public RequestScope(ShutdownService service, string requestType, string? traceId = null);
    public void Dispose();
}
```

## 数据依赖

### 内存数据结构

| 数据 | 类型 | 说明 |
|------|------|------|
| `_inFlightRequests` | `ConcurrentDictionary<string, InFlightRequest>` | 进行中请求字典 |
| `_isShuttingDown` | `volatile bool` | 停机标志 |
| `_shutdownSemaphore` | `SemaphoreSlim(0, 1)` | 停机信号量 |

### InFlightRequest 字段

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | string | 8 位 GUID 请求 ID |
| RequestType | string | 请求类型（如 "Chat"） |
| TraceId | string | 追踪 ID（默认为 requestId） |
| StartTime | DateTime | 请求开始时间（UTC） |

## DI 注册

```csharp
// ServiceCollectionExtensions.cs
services.AddSingleton<ShutdownService>();
services.AddHostedService(sp => sp.GetRequiredService<ShutdownService>());
```

注意：ShutdownService 同时注册为 Singleton 和 HostedService，确保外部可以通过 DI 获取实例调用 `RegisterRequest`/`CompleteRequest`。

## 停机流程详细时序

```
1. IHostApplicationLifetime.ApplicationStopping 触发
2. 读取 Shutdown:TimeoutSeconds 配置（默认 30）
3. 调用 ShutdownService.ShutdownAsync(timeout)
   a. 设置 _isShuttingDown = true
   b. 启动 Stopwatch
   c. 循环：
      - 若 _inFlightRequests.Count == 0 → 跳出循环
      - 若 Stopwatch.Elapsed >= timeout → 跳出循环
      - 记录剩余请求数和每个请求详情
      - 等待 min(1000ms, 剩余超时时间)
   d. 超时后仍有请求 → Warning 日志
   e. 全部完成 → Information 日志
   f. 释放 _shutdownSemaphore
4. 调用 RedisRegistry.DeregisterAsync()
```

## 关键设计决策

1. **volatile bool**：`_isShuttingDown` 使用 `volatile` 确保多线程可见性
2. **SemaphoreSlim 协调**：`ExecuteAsync` 等待信号量，`ShutdownAsync` 释放信号量，实现停机同步
3. **Singleton + HostedService**：ShutdownService 需要被 Controller 等外部组件获取，同时作为 BackgroundService 管理生命周期
4. **轮询间隔**：使用 `Math.Min(1000, remaining)` 确保最后一次轮询不会超过超时时间
5. **不强制终止**：超时后仅记录 Warning，不尝试取消或终止请求
6. **同步停机调用**：Program.cs 中使用 `.GetAwaiter().GetResult()` 同步等待停机完成

## Tasks


```json
[
  {
    "id": "GS-001",
    "title": "实现 ShutdownService 请求注册",
    "description": "RegisterRequest 生成 8 位 GUID，创建 InFlightRequest 加入 ConcurrentDictionary，停机时抛出 AgentException",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-002",
    "title": "实现 ShutdownService 请求完成",
    "description": "CompleteRequest 从字典移除请求，计算持续时间",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-003",
    "title": "实现 ShutdownAsync 等待逻辑",
    "description": "设置停机标志，轮询等待进行中请求完成，超时记录 Warning",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-004",
    "title": "实现 ShutdownService BackgroundService 生命周期",
    "description": "ExecuteAsync 等待 _shutdownSemaphore，ShutdownAsync 释放信号量",
    "status": "implemented",
    "file": "src/Engine/Services/ShutdownService.cs"
  },
  {
    "id": "GS-005",
    "title": "实现 RequestScope IDisposable 包装",
    "description": "构造时 RegisterRequest，Dispose 时 CompleteRequest，防重复释放",
    "status": "implemented",
    "file": "src/Engine/Services/RequestScope.cs"
  },
  {
    "id": "GS-006",
    "title": "DI 注册",
    "description": "ShutdownService 注册为 Singleton + HostedService",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "GS-007",
    "title": "停机编排",
    "description": "Program.cs ApplicationStopping 注册 ShutdownAsync → DeregisterAsync",
    "status": "implemented",
    "file": "src/Host/Program.cs"
  },
  {
    "id": "GS-008",
    "title": "编写 GracefulShutdown 单元测试",
    "description": "覆盖请求注册/完成、停机拒绝、超时等待、RequestScope 等场景",
    "status": "pending",
    "file": ""
  }
]
```

## Tests


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

## Conventions


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
