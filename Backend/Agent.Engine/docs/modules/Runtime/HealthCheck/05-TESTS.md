# HealthCheck - 测试文档

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
