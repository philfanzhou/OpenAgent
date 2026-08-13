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
| Agent 配置 | `agent:config:{agentId}` | Agent 运行时配置 |
| Engine 注册 | `engine:registry:{engineId}` | 服务发现 |
| LLM 注册 | `llm:registry:{providerId}` | LLM 提供商配置 |
| Skill 注册 | `skill:published:index` | 已发布技能索引 |
| RAG 注册 | `rag:published:index` | 已发布 RAG 索引 |
| 会话锁 | `lock:conversation:{tenantId}:{conversationId}` | 分布式会话锁 |
| 配置频道 | `agent:config:updates` | Agent 与 LLM 的结构化配置变更通知 |

## 当前状态

**已实现** — 生产环境使用 StackExchange.Redis，测试使用 FakeRedisConnectionProvider。

## 源码位置

- 接口：`Backend/src/OpenAgent.Engine/Abstractions/IRedisConnectionProvider.cs`
- 实现：`Backend/src/OpenAgent.Engine/Redis/`
- 健康检查：`Backend/src/OpenAgent.Engine/Redis/ConfigHealthCheck.cs`、`LlmHealthCheck.cs`、`RedisHealthCheck.cs`
