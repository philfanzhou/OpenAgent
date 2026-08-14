# Service Registration

ServiceRegistration 负责 Engine 实例在分布式环境中的自动注册与发现。每个 Engine 实例启动后生成唯一 EngineId，将自身信息写入 Redis 并周期性发送心跳，停机时主动注销。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 自动注册 | 应用启动后在 Redis 中注册 Engine 实例信息 |
| 心跳维持 | 周期性更新注册信息（含负载指标），刷新 TTL |
| 主动注销 | 停机时从 Redis 删除注册信息 |
| 孤岛模式 | Redis 不可用时降级运行，不阻塞 Engine 启动 |
| 负载上报 | 综合内存、GC、线程池压力计算负载值 |

## Architecture
```text
HeartbeatService (BackgroundService)
  → RedisRegistry.RegisterAsync / HeartbeatAsync / DeregisterAsync
  → Redis (engine:registry:{engineId}, TTL=30s)

停机: ApplicationStopping → ShutdownService.ShutdownAsync → DeregisterAsync
```

## Current Status
**Partial** — 核心功能已实现，但缺少单元测试覆盖（规划中）。

## Limits
- 心跳单线程顺序执行，`IsRegistered` 非 volatile
- 注销失败依赖 TTL 自然过期

## Source
- Interface: `Backend/src/OpenAgent.Engine/Abstractions/IEngineRegistry.cs`
- Core: `Backend/src/OpenAgent.Engine/Registry/RedisRegistry.cs`, `Backend/src/OpenAgent.Engine/Runtime/HeartbeatService.cs`
- Models: `Backend/src/OpenAgent.Engine/Models/HeartbeatOptions.cs`, `Backend/src/OpenAgent.Engine/Models/RegistryEntry.cs`
- Extensions: `Backend/src/OpenAgent.Engine/Extensions/ServiceCollectionExtensions.cs`
- Tests: 无专门测试文件
