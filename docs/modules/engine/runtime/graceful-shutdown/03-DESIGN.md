# GracefulShutdown - 设计文档

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
