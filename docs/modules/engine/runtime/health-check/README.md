
## Feature


## 核心用户故事

作为 Engine 运维人员，我希望 Engine 暴露标准健康检查端点，以便基础设施能监控 Engine 的可用性并做出路由决策。

## 功能简介

HealthCheck 实现三类健康检查，覆盖 Redis 连接、Agent 配置缓存和 LLM 配置可读性。通过 ASP.NET Core 健康检查框架注册，暴露 `/health`（live）和 `/ready`（ready）端点，支持 Kubernetes 等容器编排系统的探针检测。所有检查仅验证配置可读性，不发起真实外部 API 调用。

## 关键能力

- **Redis 连接检查**：验证 Redis 可用性和 Ping 响应
- **Agent 配置检查**：验证已发布 Agent 的配置缓存完整度
- **LLM 配置检查**：验证示例 Agent 的 LLM 配置可读性
- **分级健康状态**：Healthy / Degraded / Unhealthy 三级状态
- **标签分组**：infrastructure / ready / live 标签支持不同探针
 - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
- [../ConfigManagement/01-FEATURE.md](../ConfigManagement/01-FEATURE.md) - 配置管理（配置缓存）

## Specification


## 功能需求 (FR)

### FR-HC-001: Redis 健康检查

- **类**: `RedisHealthCheck : IHealthCheck`
- **依赖**: `IRedisConnectionProvider`
- **行为**:
  - `IsAvailable == false` → `Degraded("Redis connection not available - running in fallback mode")`
  - `PingAsync` 成功 → `Healthy("Redis connection is healthy")`
  - `PingAsync` 异常 → `Unhealthy("Redis connection failed", exception)`

### FR-HC-002: Agent 配置健康检查

- **类**: `ConfigHealthCheck : IHealthCheck`
- **依赖**: `ConfigSnapshot`、`IRedisConnectionProvider`
- **行为**:
  - Redis 不可用 → `Degraded("Redis is not available; cannot verify config snapshot freshness against published agents.")`
  - `agent:published:index` 为空 → `Degraded("No published agents found in Redis.")`
  - 所有 Agent 均在 Snapshot 中 → `Healthy("Config snapshot fully populated. {hits}/{total} agents cached.")`
  - 部分 Agent 在 Snapshot 中 → `Degraded("Config snapshot partially populated. {hits}/{total} agents cached. Sample agent: '{sampleId}'.")`
  - 无 Agent 在 Snapshot 中 → `Unhealthy("Config snapshot is empty. 0/{total} agents cached in snapshot.")`
  - 异常 → `Unhealthy("Failed to check config snapshot health.", exception)`

### FR-HC-003: LLM 配置健康检查

- **类**: `LlmHealthCheck : IHealthCheck`
- **依赖**: `IAgentConfigProvider`、`IRedisConnectionProvider`、`ILogger<LlmHealthCheck>`
- **行为**:
  - `agent:published:index` 为空 → `Degraded("No published agents found in Redis.")`
  - 示例 Agent 的 `config.Llm == null` → `Unhealthy("No LLM configuration available for agent '{sampleId}'.")`
  - 示例 Agent 有 LLM 配置 → `Healthy("ApiFormat: {format}, Model: {modelId} (verified via agent '{sampleId}')")`
  - 异常 → `Degraded("Unable to retrieve LLM configuration.", exception)`
- **重要**: 仅验证配置可读性，**不发起真实 LLM API 调用**

### FR-HC-004: 健康检查注册

- **注册标签**:
  - `redis`: infrastructure, ready, live
  - `agent-config`: ready
  - `llm-connectivity`: live
- **端点**: `/health`（live 标签）、`/ready`（ready 标签）— 由 Agent.Hosting 提供

## 验收标准 (AC)

### AC-HC-001: Redis 不可用返回 Degraded

- **Given** `IRedisConnectionProvider.IsAvailable == false`
- **When** 调用 `RedisHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Degraded`

### AC-HC-002: Redis Ping 成功返回 Healthy

- **Given** Redis 可用且 PingAsync 成功
- **When** 调用 `RedisHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Healthy`

### AC-HC-003: Redis Ping 失败返回 Unhealthy

- **Given** Redis 可用但 PingAsync 抛出异常
- **When** 调用 `RedisHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Unhealthy`，Exception 不为 null

### AC-HC-004: 配置完全缓存返回 Healthy

- **Given** `agent:published:index` 有 agent-ok，Snapshot 中有该 Agent 的 FullAgentConfig
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Healthy`，Description 包含 "fully populated"

### AC-HC-005: 配置部分缓存返回 Degraded

- **Given** `agent:published:index` 有 agent-ok 和 agent-missing，Snapshot 中仅有 agent-ok
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Degraded`，Description 包含 "partially populated"

### AC-HC-006: 配置缓存为空返回 Unhealthy

- **Given** `agent:published:index` 有 agent-missing，Snapshot 中无该 Agent 配置
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Unhealthy`，Description 包含 "empty"

### AC-HC-007: 无已发布 Agent 返回 Degraded

- **Given** `agent:published:index` 为空
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Degraded`

### AC-HC-008: LLM 配置可读返回 Healthy

- **Given** 示例 Agent 有 LLM 配置
- **When** 调用 `LlmHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Healthy`，Description 包含 ApiFormat 和 ModelId

### AC-HC-009: LLM 配置缺失返回 Unhealthy

- **Given** 示例 Agent 的 `config.Llm == null`
- **When** 调用 `LlmHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Unhealthy`

### AC-HC-010: 无已发布 Agent 时 LLM 检查返回 Degraded

- **Given** `agent:published:index` 为空
- **When** 调用 `LlmHealthCheck.CheckHealthAsync`
- **Then** 返回 `HealthStatus.Degraded`

## 健康状态决策矩阵

### RedisHealthCheck

| 条件 | 状态 |
|------|------|
| IsAvailable == false | Degraded |
| PingAsync 成功 | Healthy |
| PingAsync 异常 | Unhealthy |

### ConfigHealthCheck

| 条件 | 状态 |
|------|------|
| Redis 不可用 | Degraded |
| 无已发布 Agent | Degraded |
| 全部缓存 | Healthy |
| 部分缓存 | Degraded |
| 无缓存 | Unhealthy |
| 异常 | Unhealthy |

### LlmHealthCheck

| 条件 | 状态 |
|------|------|
| 无已发布 Agent | Degraded |
| LLM 配置存在 | Healthy |
| LLM 配置缺失 | Unhealthy |
| 异常 | Degraded |

## Design


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

## Tasks


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

## Tests


## 现有测试

### RedisHealthCheckTests（`test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs`）

| 测试方法 | 场景 |
|---------|------|
| `Returns_degraded_when_redis_not_available` | Redis 不可用 → Degraded |
| `Returns_healthy_when_ping_succeeds` | Ping 成功 → Healthy |
| `Returns_unhealthy_when_ping_throws` | Ping 抛出 RedisConnectionException → Unhealthy |

### ConfigHealthCheckTests（`test/OpenAgent.Engine.Tests/HealthChecks/ConfigHealthCheckTests.cs`）

| 测试方法 | 场景 |
|---------|------|
| `Returns_degraded_when_no_published_agents` | 无已发布 Agent → Degraded |
| `Returns_healthy_when_snapshot_fully_populated` | 全部缓存 → Healthy |
| `Returns_unhealthy_when_snapshot_is_empty` | 缓存为空 → Unhealthy |
| `Returns_degraded_when_snapshot_partially_populated` | 部分缓存 → Degraded |
| `Returns_degraded_when_redis_not_available` | Redis 不可用 → Degraded |

### LlmHealthCheckTests（`test/OpenAgent.Engine.Tests/HealthChecks/LlmHealthCheckTests.cs`）

| 测试方法 | 场景 |
|---------|------|
| `Returns_degraded_when_no_published_agents` | 无已发布 Agent → Degraded |
| `Returns_unhealthy_when_llm_config_missing` | LLM 配置缺失 → Unhealthy |
| `Returns_healthy_when_llm_config_available` | LLM 配置存在 → Healthy |

## 缺失测试场景

### TC-HC-001: ConfigHealthCheck 异常处理

- **Given** `SetMembersAsync` 抛出异常
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** 返回 `Unhealthy`，Description 包含 "Failed to check config snapshot health"

### TC-HC-002: LlmHealthCheck 异常处理

- **Given** `GetConfigAsync` 抛出异常
- **When** 调用 `LlmHealthCheck.CheckHealthAsync`
- **Then** 返回 `Degraded`，Description 包含 "Unable to retrieve LLM configuration"

### TC-HC-003: LlmHealthCheck Healthy 描述包含详细信息

- **Given** 示例 Agent 有 LLM 配置（ApiFormat=OpenAICompatible, ModelId=gpt-4o）
- **When** 调用 `LlmHealthCheck.CheckHealthAsync`
- **Then** Description 包含 "OpenAICompatible" 和 "gpt-4o"

### TC-HC-004: ConfigHealthCheck Healthy 描述包含缓存统计

- **Given** 2 个已发布 Agent 均已缓存
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** Description 包含 "2/2 agents cached"

### TC-HC-005: ConfigHealthCheck 部分缓存描述包含样本 Agent

- **Given** 2 个已发布 Agent，1 个已缓存
- **When** 调用 `ConfigHealthCheck.CheckHealthAsync`
- **Then** Description 包含 "1/2 agents cached" 和样本 Agent ID

### TC-HC-006: RedisHealthCheck Unhealthy 包含异常

- **Given** PingAsync 抛出 RedisConnectionException
- **When** 调用 `RedisHealthCheck.CheckHealthAsync`
- **Then** `result.Exception` 不为 null

### TC-HC-007: LlmHealthCheck 使用第一个 Agent 作为样本

- **Given** `agent:published:index` 包含多个 Agent
- **When** 调用 `LlmHealthCheck.CheckHealthAsync`
- **Then** 使用第一个 Agent 的配置进行检查 [推断]

### TC-HC-008: 健康检查标签验证

- **Given** Engine 启动
- **When** 查询健康检查注册
- **Then** redis 检查有 infrastructure/ready/live 标签，agent-config 有 ready 标签，llm-connectivity 有 live 标签

## 测试基础设施

### FakeRedisConnectionProvider

测试中使用了多个不同的 `FakeRedisConnectionProvider` 实现：

1. **RedisHealthCheckTests** 中的版本：支持 `IsAvailableValue` 和 `PingException` 属性
2. **ConfigHealthCheckTests** 中的版本：支持 `AddSetMember` 方法和 `_isAvailable` 构造参数
3. **LlmHealthCheckTests** 中的版本：支持 `AddSetMember` 方法

> 建议统一为共享的 TestDoubles 实现，减少重复代码。

## Conventions


## 命名约定

### 类命名

- 健康检查类后缀 `HealthCheck`：`RedisHealthCheck`、`ConfigHealthCheck`、`LlmHealthCheck`
- 位于 `Redis` 命名空间下（因为依赖 `IRedisConnectionProvider`）

### 检查名称

- 使用小写短横线格式：`redis`、`agent-config`、`llm-connectivity`
- 与 ASP.NET Core 健康检查注册名称一致

## 健康状态约定

### 状态语义

| 状态 | 含义 | 使用场景 |
|------|------|---------|
| Healthy | 功能完全正常 | Redis 连接正常、配置完整、LLM 配置可读 |
| Degraded | 功能降级但仍可用 | Redis 不可用（孤岛模式）、配置部分缓存、无已发布 Agent |
| Unhealthy | 功能不可用 | Redis Ping 失败、配置缓存为空、LLM 配置缺失 |

### 状态决策原则

1. **Redis 不可用 = Degraded**：Engine 可在无 Redis 时运行，不是致命问题
2. **配置缓存为空 = Unhealthy**：无任何配置意味着 Engine 无法处理请求
3. **LLM 配置缺失 = Unhealthy**：无 LLM 配置意味着 Agent 无法执行
4. **异常处理策略不一致**：
   - ConfigHealthCheck 异常 → Unhealthy（配置检查失败是严重问题）
   - LlmHealthCheck 异常 → Degraded（可能是临时问题）

## Description 消息约定

### 格式

- 使用完整英文句子或短语
- 包含具体数据：缓存比例、样本 Agent ID、ApiFormat、ModelId

### 示例

| 检查 | 状态 | Description |
|------|------|------------|
| Redis | Degraded | `"Redis connection not available - running in fallback mode"` |
| Redis | Healthy | `"Redis connection is healthy"` |
| Redis | Unhealthy | `"Redis connection failed"` |
| Config | Healthy | `"Config snapshot fully populated. 3/3 agents cached."` |
| Config | Degraded | `"Config snapshot partially populated. 2/3 agents cached. Sample agent: 'agent-a'."` |
| Config | Unhealthy | `"Config snapshot is empty. 0/3 agents cached in snapshot."` |
| LLM | Healthy | `"ApiFormat: OpenAICompatible, Model: gpt-4o (verified via agent 'agent-a')"` |
| LLM | Unhealthy | `"No LLM configuration available for agent 'agent-a'."` |
| LLM | Degraded | `"Unable to retrieve LLM configuration."` |

## 日志约定

### LlmHealthCheck 日志

| 场景 | 级别 | 示例 |
|------|------|------|
| 检查失败 | Warning | `"LLM health check failed"` |

> RedisHealthCheck 和 ConfigHealthCheck 不记录日志（无 ILogger 依赖）。

## 依赖注入约定

### 构造函数依赖

| 检查类 | 依赖 |
|--------|------|
| RedisHealthCheck | `IRedisConnectionProvider` |
| ConfigHealthCheck | `ConfigSnapshot`、`IRedisConnectionProvider` |
| LlmHealthCheck | `IAgentConfigProvider`、`IRedisConnectionProvider`、`ILogger<LlmHealthCheck>` |

### 注册方式

```csharp
services.AddHealthChecks()
    .AddCheck<THealthCheck>(name, tags: new[] { ... });
```

- 使用 `AddCheck<T>` 泛型注册，运行时自动从 DI 容器解析依赖
- 标签使用字符串数组

## 标签约定

| 标签 | 含义 | 检查项 |
|------|------|--------|
| `infrastructure` | 基础设施检查 | redis |
| `ready` | 就绪探针 | redis, agent-config |
| `live` | 存活探针 | redis, llm-connectivity |

## internal 访问级别

- 所有健康检查类均为 `internal`，不对外暴露
- 通过 ASP.NET Core 健康检查框架的 `AddCheck<T>` 泛型注册，框架可访问 internal 类
