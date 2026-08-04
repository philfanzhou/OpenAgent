# HealthCheck - 任务清单

```json
[
  {
    "id": "HC-001",
    "title": "实现 RedisHealthCheck",
    "description": "检查 Redis IsAvailable 和 PingAsync，返回 Degraded/Healthy/Unhealthy",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisHealthCheck.cs"
  },
  {
    "id": "HC-002",
    "title": "实现 ConfigHealthCheck",
    "description": "检查 agent:published:index 与 Snapshot 缓存完整度，返回 Healthy/Degraded/Unhealthy",
    "status": "implemented",
    "file": "src/Engine/Redis/ConfigHealthCheck.cs"
  },
  {
    "id": "HC-003",
    "title": "实现 LlmHealthCheck",
    "description": "检查示例 Agent 的 LLM 配置可读性，返回 Healthy/Degraded/Unhealthy",
    "status": "implemented",
    "file": "src/Engine/Redis/LlmHealthCheck.cs"
  },
  {
    "id": "HC-004",
    "title": "DI 注册健康检查",
    "description": "注册 redis(infrastructure,ready,live)、agent-config(ready)、llm-connectivity(live)",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "HC-005",
    "title": "编写 RedisHealthCheckTests",
    "description": "测试 Redis 不可用/Degraded、Ping 成功/Healthy、Ping 失败/Unhealthy",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs"
  },
  {
    "id": "HC-006",
    "title": "编写 ConfigHealthCheckTests",
    "description": "测试无已发布 Agent/Degraded、完全缓存/Healthy、部分缓存/Degraded、空缓存/Unhealthy、Redis 不可用/Degraded",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/HealthChecks/ConfigHealthCheckTests.cs"
  },
  {
    "id": "HC-007",
    "title": "编写 LlmHealthCheckTests",
    "description": "测试无已发布 Agent/Degraded、LLM 配置缺失/Unhealthy、LLM 配置存在/Healthy",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/HealthChecks/LlmHealthCheckTests.cs"
  }
]
```
