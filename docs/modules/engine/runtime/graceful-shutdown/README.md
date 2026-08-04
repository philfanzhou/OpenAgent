# Graceful Shutdown

GracefulShutdown 通过跟踪进行中的请求，确保 Engine 停机时不会中断正在处理的请求。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 请求跟踪 | `ConcurrentDictionary` 跟踪所有进行中请求 |
| 拒绝新请求 | 停机时新请求抛出 `AgentException(DependencyUnavailable)` |
| 等待完成 | 停机时轮询等待进行中请求完成 |
| 超时保护 | 超时（`Shutdown:TimeoutSeconds`，默认 30 秒）后记录 Warning |
| RequestScope | `IDisposable` 包装，自动注册/完成请求 |

## Architecture
```text
ApplicationStopping
  → ShutdownService.ShutdownAsync(timeout)
  → RedisRegistry.DeregisterAsync()

请求入口 → RequestScope(RegisterRequest) → 执行 → Dispose(CompleteRequest)
```

## Current Status
**Partial** — 核心功能已实现，但缺少单元测试覆盖（规划中）。

## Limits
- 超时后仅记录 Warning，不强制终止请求
- 停机顺序依赖 `Program.cs` 编排

## Source
- Core: `src/Engine/Services/ShutdownService.cs`, `RequestScope.cs`
- Orchestration: `src/Host/Program.cs`
- Extensions: `src/Engine/Extensions/ServiceCollectionExtensions.cs`
- Tests: 无专门测试文件（待补充）
