# Config Management

ConfigManagement 负责 Agent 配置的加载、缓存与提供。默认读取链仍是内存快照 → Redis → Mock；
默认关闭的 PostgreSQL 验证模式改为内存快照 → Redis 派生缓存 → PostgreSQL → Mock。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 三级读取链 | Snapshot → Redis → Mock 降级，逐级回退 |
| 内存缓存 | `ConfigSnapshot` 基于 `IMemoryCache`，支持按 key 或 agentId+configType 存取 |
| 全量写入 | `SetFullConfig` 一次性写入所有子配置片段及版本号 |
| 敏感信息注入 | 从环境变量 `LLM__APIKEY` / `LLM_API_KEY` 填充 LLM API Key |
| Mock 降级 | 开发/测试环境自动启用 Mock Agent |
| Agent 列表 | 从 `agent:published:index` 获取已发布 Agent 列表 |
| PostgreSQL 验证 | `agent_configurations` 保存完整 JSONB 与单调版本，Redis 缓存使用独立 key 和 TTL |
| 启动回填 | 后台服务等待 Redis 可用后从 PostgreSQL 重建 Agent 派生缓存 |

## Architecture
```text
ConfigProvider.GetConfigAsync(agentId)
  ├─ 1. ConfigSnapshot → 命中即返回
  ├─ 2a. 默认：agent:config:{id}
  ├─ 2b. 验证：agent:config-cache:{id} → PostgreSQL → 回填 Redis
  ├─ 3. AllowMockAgent → Mock 配置
  └─ 4. null / 持久化依赖错误
```

## Current Status
**Implemented / opt-in proof** — Redis 模式保持默认；设置
`ConfigurationStore:UsePostgreSqlForAgents=true` 后才启用 PostgreSQL Agent 配置源。该开关不导入旧配置，启用前必须单独迁移数据。

## Limits
- `GetConfigAsync()` 无 agentId 重载始终抛出 `InvalidOperationException`
- PostgreSQL 验证只覆盖 Agent 完整配置；LLM、RAG 独立目录和 MCP 未迁移
- PostgreSQL 提交与 Redis 缓存/Pub/Sub 不构成跨存储事务，缓存失败由 TTL、回源和启动回填收敛

## Source
- Interface: `Backend/src/OpenAgent.Engine/Abstractions/IConfigSnapshot.cs`
- Core: `Backend/src/OpenAgent.Engine/Config/ConfigProvider.cs`, `Backend/src/OpenAgent.Engine/Models/ConfigSnapshot.cs`
- Extensions: `Backend/src/OpenAgent.Engine/Extensions/ServiceCollectionExtensions.cs`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Config/`
