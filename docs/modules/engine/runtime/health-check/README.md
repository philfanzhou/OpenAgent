# Health Check

HealthCheck 实现三类健康检查，覆盖 Redis 连接、Agent 配置缓存和 LLM 配置可读性。通过 ASP.NET Core 健康检查框架注册，暴露 `/health`（live）和 `/ready`（ready）端点。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| Redis 检查 | 验证 Redis 可用性和 Ping 响应 |
| Agent 配置检查 | 验证已发布 Agent 的配置缓存完整度 |
| LLM 配置检查 | 验证示例 Agent 的 LLM 配置可读性（不发起真实 API 调用）|
| 分级状态 | Healthy / Degraded / Unhealthy 三级 |
| 标签分组 | infrastructure / ready / live 标签支持不同探针 |

## Health Checks
| Check | Tags | Endpoint |
|-------|------|----------|
| redis | infrastructure, ready, live | /health + /ready |
| agent-config | ready | /ready |
| llm-connectivity | live | /health |

## Current Status
**Implemented** — 所有检查和测试均已落地。

## Limits
- 仅验证配置可读性，不发起真实外部 API 调用
- LlmHealthCheck 仅检查第一个 Agent 作为样本
- Redis 不可用返回 Degraded（非 Unhealthy），Engine 可在孤岛模式运行

## Source
- Core: `src/Engine/Redis/RedisHealthCheck.cs`, `ConfigHealthCheck.cs`, `LlmHealthCheck.cs`
- Extensions: `src/Engine/Extensions/ServiceCollectionExtensions.cs`
- Tests: `test/OpenAgent.Engine.Tests/HealthChecks/`
