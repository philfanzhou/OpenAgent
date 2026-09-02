# Redis 集成（Engine 视角）

Engine 通过 Redis 实现配置热加载、引擎注册/心跳、技能/LLM/RAG 注册发现以及 Pub/Sub 配置变更通知。

## 核心能力

| 能力 | 说明 |
|------|------|
| 弹性连接 | 连接失败后自动重试（5 秒间隔），不阻塞启动 |
| 孤岛模式降级 | Redis 不可用时，读操作返回空值，写操作返回 false，服务不中断 |
| 配置热加载 | Pub/Sub 监听配置变更，实时更新 Engine 本地快照 |
| 服务注册与心跳 | Engine 启动时注册自身信息，定期心跳续约 |
| 健康检查 | 通过 `RedisHealthCheck` 暴露 Degraded / Healthy / Unhealthy 三级状态 |

## 架构

```
Engine Host
  ├─ RedisConnectionProvider (StackExchange.Redis)
  ├─ RedisRegistry (注册 + 心跳)
  ├─ HotReloadService (Pub/Sub 配置热加载)
  ├─ ConfigProvider (配置读取)
  └─ RedisHealthCheck
```

## 关键 Redis Key 模式

| 类别 | Key 格式 | 用途 |
|------|----------|------|
| Agent 派生缓存 | `agent:config-cache:{tenantId}:{agentId}` | PostgreSQL Agent 配置的可删除热缓存，TTL 默认 300 秒 |
| Agent 派生缓存索引 | `agent:config-cache:index:{tenantId}` | 按租户维护的诊断索引，不参与事实读取 |
| Engine 注册 | `engine:registry:{engineId}` | 服务发现 |
| Engine 注册索引 | `engine:registry:index` | Router 有界读取注册项，避免 Redis 全库键枚举 |
| LLM 注册 | `llm:registry:{providerId}` | LLM 提供商配置 |
| Skill 注册 | `skill:published:index` | 已发布技能索引 |
| MCP 注册 | `mcp:published:index` | 独立维护的 MCP Server 配置索引 |
| RAG 注册 | `rag:published:index` | 已发布 RAG 索引 |
| 会话锁 | `openagent:conversation-lock:{tenantId}:{conversationId}` | 分布式会话锁 |
| 配置频道 | `agent:config:updates` | 携带租户 ID 的 Agent 与 LLM 结构化变更通知 |

## 当前状态

**已实现** — PostgreSQL 是 Agent 配置事实源，Redis 仅保存按租户隔离且带 TTL 的派生缓存。配置更新在数据库
提交成功后立即刷新缓存并发布通知；后台协调服务默认每 60 秒从 PostgreSQL 重新投影一次，以修复 Redis
断线期间遗漏的缓存或 Pub/Sub 事件。Redis 不可用时仍可直接从 PostgreSQL 读取和写入。

LLM、RAG、MCP 和 Skill 目录仍沿用各自现有存储边界；本次迁移不把这些目录整体迁入 PostgreSQL。

## 源码位置

- 接口：`Backend/src/OpenAgent.Engine/Abstractions/IRedisConnectionProvider.cs`
- 实现：`Backend/src/OpenAgent.Engine/Redis/`
- 健康检查：`Backend/src/OpenAgent.Engine/Redis/ConfigHealthCheck.cs`、`LlmHealthCheck.cs`、`RedisHealthCheck.cs`
