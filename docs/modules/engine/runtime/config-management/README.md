# Config Management

ConfigManagement 负责 Agent 配置的加载、缓存与提供。采用三级读取链（内存快照 → Redis → Mock 降级），确保在各种可用性场景下都能返回配置。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 三级读取链 | Snapshot → Redis → Mock 降级，逐级回退 |
| 内存缓存 | `ConfigSnapshot` 基于 `IMemoryCache`，支持按 key 或 agentId+configType 存取 |
| 全量写入 | `SetFullConfig` 一次性写入所有子配置片段及版本号 |
| 敏感信息注入 | 从环境变量 `LLM__APIKEY` / `LLM_API_KEY` 填充 LLM API Key |
| Mock 降级 | 开发/测试环境自动启用 Mock Agent |
| Agent 列表 | 从 `agent:published:index` 获取已发布 Agent 列表 |

## Architecture
```text
ConfigProvider.GetConfigAsync(agentId)
  ├─ 1. LoadFromSnapshot → 命中即返回
  ├─ 2. Redis.IsAvailable → LoadFromRedisAsync → SetFullConfig
  ├─ 3. AllowMockAgent → Mock 配置
  └─ 4. null
```

## Current Status
**Implemented** — 完整实现，含版本管理、敏感信息注入和 Mock 降级。

## Limits
- `GetConfigAsync()` 无 agentId 重载始终抛出 `InvalidOperationException`
- 版本号与配置分离存储，`GetVersion` 缺失 key 时返回 0 而非 null

## Source
- Interface: `Backend/src/OpenAgent.Engine/Abstractions/IConfigSnapshot.cs`
- Core: `Backend/src/OpenAgent.Engine/Config/ConfigProvider.cs`, `Backend/src/OpenAgent.Engine/Models/ConfigSnapshot.cs`
- Extensions: `Backend/src/OpenAgent.Engine/Extensions/ServiceCollectionExtensions.cs`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Config/`
