# 数据所有权

## 概述

Agent.Engine **不使用关系型数据库**（无 Entity Framework），所有外部状态存储在 Redis 中。本文档明确 Engine 对各数据实体的读写权限。

## 数据实体分类

### Engine 拥有（可读写）

| 实体 | Redis Key | 格式 | 写入时机 | 读取时机 | TTL |
|------|-----------|------|----------|----------|-----|
| Engine 注册条目 | `engine:registry:{engineId}` | `RegistryEntry` JSON | 启动注册、心跳续约 | 心跳检查自身状态 | `Heartbeat:RegistryTtlSeconds`（默认 30s） |

`RegistryEntry` 字段：

```json
{
  "EngineId": "a1b2c3d4",
  "Host": "engine-pod-01",
  "Port": 5208,
  "Load": 42,
  "LastHeartbeat": "2026-06-10T06:30:00Z"
}
```

### Engine 只读（由 Agent.Matrix 管理）

| 实体 | Redis Key | 格式 | 读取者 | 说明 |
|------|-----------|------|--------|------|
| Agent 配置 | `agent:config:{agentId}` | `AgentConfigEntity` JSON | `ConfigProvider`, `HotReloadService` | 包含 `Config`、`CurrentVersion`、`AgentId`、`Name`、`Status` |
| 已发布 Agent 索引 | `agent:published:index` | SET of agentId | `ConfigProvider`, `ConfigHealthCheck`, `LlmHealthCheck` | 已发布的 Agent ID 集合 |
| Skill 定义 | `skill:registry:{skillName}` | `SkillInstanceConfig` JSON | `RedisSkillRegistrar` | Skill 元数据与端点配置 |
| 已发布 Skill 索引 | `skill:published:index` | SET of skillName | `RedisSkillRegistrar` | 已发布的 Skill 名称集合 |
| LLM Profile | `llm:registry:{profileId}` | `LlmProviderProfile` JSON | `RedisLlmRegistrar` | LLM 提供者配置 |
| 已发布 LLM 索引 | `llm:published:index` | SET of profileId | `RedisLlmRegistrar` | 已发布的 LLM Profile 集合 |
| RAG 实例 | `rag:registry:{instanceId}` | `RagInstanceConfig` JSON | `RedisRagRegistrar` | RAG 实例配置 |
| 已发布 RAG 索引 | `rag:published:index` | SET of instanceId | `RedisRagRegistrar` | 已发布的 RAG 实例集合 |

### Engine 内存缓存（非 Redis）

| 实体 | 存储位置 | 管理者 | 说明 |
|------|----------|--------|------|
| Agent 配置快照 | `ConfigSnapshot` (`IMemoryCache`) | `ConfigProvider` 写入，`HotReloadService` 更新 | 按 `agent:{agentId}:config:{configType}` 缓存 |
| 配置版本号 | `ConfigSnapshot` (`IMemoryCache`) | `HotReloadService` 写入 | 按 `agent:{agentId}:config:{configType}:version` 缓存 |

## 写入禁止规则

**Engine 严禁写入以下 Redis Key：**

| 禁止写入的 Key | 原因 | 正确管理者 |
|----------------|------|------------|
| `agent:config:*` | Agent 配置由管理平台统一管理 | Agent.Matrix |
| `agent:published:index` | 发布索引由管理平台维护 | Agent.Matrix |
| `skill:registry:*` | Skill 定义由管理平台注册 | Agent.Matrix |
| `skill:published:index` | Skill 发布索引由管理平台维护 | Agent.Matrix |
| `llm:registry:*` | LLM Profile 由管理平台注册 | Agent.Matrix |
| `llm:published:index` | LLM 发布索引由管理平台维护 | Agent.Matrix |
| `rag:registry:*` | RAG 实例由管理平台注册 | Agent.Matrix |
| `rag:published:index` | RAG 发布索引由管理平台维护 | Agent.Matrix |

**Engine 唯一可写的 Redis Key 前缀：** `engine:registry:`

## 数据流方向

```
Agent.Matrix (写入)                Agent.Engine (只读)               Agent.Engine (写入)
       |                                |                                  |
       |  SET agent:config:*            |  GET agent:config:*              |  SET engine:registry:*
       |  SET skill:registry:*          |  GET skill:registry:*            |  DEL engine:registry:*
       |  SET llm:registry:*            |  GET llm:registry:*              |
       |  SET rag:registry:*            |  GET rag:registry:*              |
       |  SADD *:published:index        |  SMEMBERS *:published:index      |
       |  PUBLISH agent:config:updates  |  SUBSCRIBE agent:config:updates  |
       v                                v                                  v
   +---------------------------------------------------------------------------+
   |                              Redis                                        |
   +---------------------------------------------------------------------------+
```

## 配置快照缓存策略

`ConfigSnapshot` 使用 `IMemoryCache` 存储配置，缓存 Key 格式：

| 缓存类型 | Key 格式 | 示例 |
|----------|----------|------|
| 完整配置 | `agent:{agentId}:config:FullAgentConfig` | `agent:my-agent:config:FullAgentConfig` |
| LLM 配置 | `agent:{agentId}:config:LLMSettings` | `agent:my-agent:config:LLMSettings` |
| RAG 配置 | `agent:{agentId}:config:RAGSettings` | `agent:my-agent:config:RAGSettings` |
| MCP 配置 | `agent:{agentId}:config:MCPSettings` | `agent:my-agent:config:MCPSettings` |
| Skills 配置 | `agent:{agentId}:config:SkillsSettings` | `agent:my-agent:config:SkillsSettings` |
| 版本号 | `agent:{agentId}:config:{configType}:version` | `agent:my-agent:config:FullAgentConfig:version` |

**缓存更新路径：**
1. 首次请求：`ConfigProvider.GetConfigAsync()` 从 Redis 加载 → 写入 `ConfigSnapshot`
2. 热加载：`HotReloadService` 收到 Pub/Sub → 更新 `ConfigSnapshot`
3. 全量刷新：`SetFullConfig()` 同时写入 FullAgentConfig + LLM/RAG/MCP/Skills 子配置
