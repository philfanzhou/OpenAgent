# HealthCheck - 功能规格说明

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
