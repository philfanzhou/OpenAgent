# Config Management

ConfigManagement 负责 Agent 配置的加载、缓存与提供。读取链为租户内存快照 → 租户 Redis 派生缓存 →
PostgreSQL → Mock；PostgreSQL 是 Agent 配置的唯一事实源。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 四级读取链 | Snapshot → Redis cache → PostgreSQL → Mock，逐级回退 |
| 内存缓存 | `ConfigSnapshot` 基于 `IMemoryCache`，使用 tenantId+agentId 作用域 |
| 全量写入 | `SetFullConfig` 一次性写入所有子配置片段及版本号 |
| 敏感信息解析 | 授权后按 tenantId + `ApiKeySecretRef` 从 `IAgentSecretResolver` 获取 Key，持久化配置不含明文 |
| Mock 降级 | 开发/测试环境自动启用 Mock Agent |
| Agent 列表 | 从 PostgreSQL 获取，并按租户过滤 |
| PostgreSQL 事实源 | `agent_configurations` 以 `(TenantId, AgentId)` 为主键，保存 JSONB 与单调版本 |
| Redis 缓存 | `agent:config-cache:{tenantId}:{agentId}` 使用可配置 TTL，默认 300 秒 |
| 持续协调 | 后台服务启动时及之后按周期从 PostgreSQL 重建 Agent 派生缓存，默认 60 秒 |

## Architecture
```text
ConfigProvider.GetConfigAsync(tenantId, agentId)
  ├─ 1. ConfigSnapshot[tenantId, agentId] → 命中即返回
  ├─ 2. agent:config-cache:{tenantId}:{agentId} → 命中即返回
  ├─ 3. PostgreSQL[(tenantId, agentId)] → 回填 Redis 与 Snapshot
  ├─ 4. AllowMockAgent → Mock 配置
  └─ 5. null / 持久化依赖错误
```

## Current Status
**Implemented** — PostgreSQL Agent 配置源始终启用。历史 `agent:config:*` Redis 数据不会自动迁移，部署前必须
先把现有 Agent 配置导入 `openagent.agent_configurations`，再切换流量。

配置中的密钥字段使用引用，例如 `ApiKeySecretRef=llm:openai-prod`。默认解析器读取
`Secrets:{tenantId}:{secretRef}`；环境变量可写为
`Secrets__tenant-a__llm__openai-prod=<secret>`。生产环境可以替换 `IAgentSecretResolver`，接入 Vault、
Azure Key Vault 或其他密钥服务。

## Limits
- PostgreSQL 迁移只覆盖 Agent 完整配置；LLM、RAG 独立目录和 MCP 未整体迁移
- PostgreSQL 提交与 Redis 缓存/Pub/Sub 不构成跨存储事务；缓存失败由回源和周期协调收敛
- Redis Pub/Sub 是至多一次通知，不提供审计级事件投递；如需严格事件交付需增加数据库 outbox

## Source
- Interface: `Backend/src/OpenAgent.Engine/Abstractions/IConfigSnapshot.cs`
- Core: `Backend/src/OpenAgent.Engine/Config/ConfigProvider.cs`, `Backend/src/OpenAgent.Engine/Models/ConfigSnapshot.cs`
- Extensions: `Backend/src/OpenAgent.Engine/Extensions/ServiceCollectionExtensions.cs`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Config/`
