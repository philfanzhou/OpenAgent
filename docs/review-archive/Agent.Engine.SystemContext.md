# 系统上下文

> `Agent.Workflow` 尚未纳入仓库；相关描述是目标架构，见
> [Agent.Workflow 状态](../../../Agent.Workflow.md)。

## 服务定位

`Agent.Engine` 是 Agent 平台的**生产运行时宿主**，负责：

- 暴露 HTTP / NDJSON / SSE 访问入口
- 承载 `Agent.Core` 的执行管线与多引擎实现
- 接入 `Agent.Hosting` 提供的 JWT 鉴权、健康检查和 OpenTelemetry 宿主能力
- 完成 Redis 服务注册、心跳续约、配置热加载与优雅停机

它**不是**：

- 路由分发与负载均衡 → `Agent.Router`
- 复杂工作流编排 → `Agent.Workflow`
- 会话记录持久化 → `Agent.Core` 及其存储实现
- Agent 配置管理 → `Agent.Matrix`

## 服务标识

| 属性 | 值 |
|------|-----|
| 服务名 | `agent-engine` |
| HTTP 端口 | 5208 |
| HTTPS 端口 | 7175 |
| 框架 | .NET 8.0 |
| 入口程序集 | `OpenAgent.Core.Engine.Host.dll` |
| OpenTelemetry Source | `OpenAgent.Engine` |

## 上下文图

```
                    +-------------------+
                    |   Agent.Matrix    |  (配置管理，写入 Redis)
                    +-------------------+
                             |
                             | 写入 agent:config:*, skill:*, llm:*, rag:*
                             v
+----------+        +------------------+        +-------------------+
|  Agent.  |  HTTP  |   Agent.Engine   | Redis  |      Redis        |
|  Router  |------->|   (本服务)       |<------>|  (注册/配置/PubSub)|
+----------+        +------------------+        +-------------------+
                             |
+----------+        +--------+--------+        +-------------------+
|  Agent.  |  HTTP  |   Agent.Core    |  HTTP  |  外部 LLM API     |
| Channels |------->|   (执行管线)     |------->|  外部 RAG 服务     |
+----------+        +------------------+        |  外部 MCP 服务器   |
                             |                  +-------------------+
                             v
                    +-------------------+
                    |  Skill HTTP 端点  |  (通过 RedisSkillRegistrar 调用)
                    +-------------------+
```

## 上下游关系

### 上游调用方

| 调用方 | 交互方式 | 说明 |
|--------|----------|------|
| Agent.Router | HTTP (JWT) | 路由分发后的最终执行节点 |
| Agent.Channels | HTTP (JWT) | Outlook/Teams 消息转发至 Engine |
| 任意 HTTP 客户端 | HTTP (JWT) | 携带有效 JWT Token 即可调用 |

### 下游依赖

| 依赖 | 交互方式 | 说明 |
|------|----------|------|
| Redis | TCP | 服务注册、配置存储、Pub/Sub 热加载 |
| Agent.Core | 进程内调用 | 执行管线、Skill/MCP/RAG 编排 |
| Agent.Hosting | 进程内调用 | JWT 认证、健康检查、OpenTelemetry |
| 外部 LLM API | HTTP | 通过 Agent.Core 的引擎实现调用 |
| 外部 RAG 服务 | HTTP | 通过 Agent.Core 调用 |
| 外部 MCP 服务器 | HTTP/SSE | 通过 Agent.Core 调用 |
| Skill HTTP 端点 | HTTP | 通过 `RedisSkillRegistrar` 的 `RedisMockSkill` 调用 |

## 职责边界

### Engine vs Core

| 层 | 职责 |
|----|------|
| Agent.Engine | 宿主、端点、JWT 身份认证、健康检查、配置热加载、注册发现、优雅停机 |
| Agent.Core | 执行管线、工具编排、运行时鉴权、租户隔离、RAG/Skill/MCP 调用 |

### Engine vs Router

| 层 | 职责 |
|----|------|
| Agent.Router | 流量入口、实例选择、负载分发 |
| Agent.Engine | 被 Router 调度后的最终执行节点 |

### 认证与鉴权分层

- **Engine**：JWT Bearer 身份认证 + Claims 解析 → 映射为 `IAgentUserContext`
- **Core**：运行时权限校验、租户隔离、工具/RAG 细粒度鉴权

### 会话边界

- `conversationId` 由上游（Channels / Router）生成并携带
- Engine 只负责透传，不拥有会话存储
- Core 负责加载、构建和保存会话上下文
