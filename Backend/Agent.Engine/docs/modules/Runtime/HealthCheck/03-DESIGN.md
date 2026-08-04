# HealthCheck - 设计文档

## 架构概览

```
┌────────────────┐     CheckHealthAsync     ┌──────────────────┐
│  ASP.NET Core   │ ──────────────────────→ │  Health Checks   │
│  Health Check   │                          │                  │
│  Middleware     │                          │  ┌────────────┐  │
│  (/health,      │                          │  │ RedisHC    │  │
│   /ready)       │                          │  │ (live+ready)│  │
└────────────────┘                          │  └────────────┘  │
                                            │  ┌────────────┐  │
                                            │  │ ConfigHC   │  │
                                            │  │ (ready)    │  │
                                            │  └────────────┘  │
                                            │  ┌────────────┐  │
                                            │  │ LlmHC      │  │
                                            │  │ (live)     │  │
                                            │  └────────────┘  │
                                            └──────────────────┘
```

## 文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/Engine/Redis/RedisHealthCheck.cs` | Redis 连接健康检查 |
| `src/Engine/Redis/ConfigHealthCheck.cs` | Agent 配置缓存健康检查 |
| `src/Engine/Redis/LlmHealthCheck.cs` | LLM 配置可读性健康检查 |
| `src/Engine/Extensions/ServiceCollectionExtensions.cs` | DI 注册 |

## 类定义

### RedisHealthCheck

```csharp
internal class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisConnectionProvider _redis;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}
```

### ConfigHealthCheck

```csharp
internal class ConfigHealthCheck : IHealthCheck
{
    private readonly ConfigSnapshot _snapshot;
    private readonly IRedisConnectionProvider _redis;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}
```

### LlmHealthCheck

```csharp
internal class LlmHealthCheck : IHealthCheck
{
    private readonly IAgentConfigProvider _configProvider;
    private readonly IRedisConnectionProvider _redis;
    private readonly ILogger<LlmHealthCheck> _logger;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}
```

## 数据依赖

### Redis 读取

| Key | 类型 | 检查类 | 用途 |
|-----|------|--------|------|
| `agent:published:index` | Set | ConfigHealthCheck, LlmHealthCheck | 获取已发布 Agent 列表 |

### 内存读取

| 数据源 | 检查类 | 用途 |
|--------|--------|------|
| `ConfigSnapshot` | ConfigHealthCheck | 检查 Agent 配置是否已缓存 |
| `IAgentConfigProvider` | LlmHealthCheck | 获取示例 Agent 的 LLM 配置 |

## DI 注册

```csharp
// ServiceCollectionExtensions.cs
services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: new[] { "infrastructure", "ready", "live" })
    .AddCheck<ConfigHealthCheck>("agent-config", tags: new[] { "ready" })
    .AddCheck<LlmHealthCheck>("llm-connectivity", tags: new[] { "live" });
```

## 健康检查标签与端点映射

| 检查名称 | 标签 | 推断端点 |
|---------|------|---------|
| redis | infrastructure, ready, live | /health + /ready |
| agent-config | ready | /ready |
| llm-connectivity | live | /health |

> 端点映射由 Agent.Hosting 包提供，Engine 本身不直接配置端点。

## 检查逻辑流程

### ConfigHealthCheck

```
CheckHealthAsync
  │
  ├─ Redis 不可用? → Degraded
  │
  ├─ 读取 agent:published:index
  │   └─ 为空? → Degraded
  │
  ├─ 遍历每个 agentId，检查 Snapshot 中是否有 FullAgentConfig
  │
  ├─ snapshotHits == total? → Healthy
  ├─ snapshotHits > 0? → Degraded
  └─ snapshotHits == 0? → Unhealthy
```

### LlmHealthCheck

```
CheckHealthAsync
  │
  ├─ 读取 agent:published:index
  │   └─ 为空? → Degraded
  │
  ├─ 取第一个 Agent 作为样本
  │
  ├─ configProvider.GetConfigAsync(sampleAgentId)
  │   ├─ config.Llm == null? → Unhealthy
  │   └─ config.Llm != null? → Healthy
  │
  └─ 异常? → Degraded
```

## 关键设计决策

1. **仅验证配置可读性**：LlmHealthCheck 不发起真实 LLM API 调用，避免健康检查导致额外费用或延迟
2. **使用第一个 Agent 作为样本**：LlmHealthCheck 仅检查一个 Agent 的 LLM 配置，而非遍历所有 Agent
3. **ConfigHealthCheck 直接依赖 ConfigSnapshot**：而非通过 IConfigSnapshot 接口，与 ConfigManagement 一致
4. **Redis 不可用返回 Degraded 而非 Unhealthy**：Engine 可在无 Redis 时运行（孤岛模式），因此 Redis 不可用不是致命问题
5. **异常处理策略不同**：ConfigHealthCheck 异常返回 Unhealthy，LlmHealthCheck 异常返回 Degraded
