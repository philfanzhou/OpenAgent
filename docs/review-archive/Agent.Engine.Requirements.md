# 服务级需求摘要

> `Agent.Workflow` 尚未纳入仓库；相关归属是目标架构，见
> [Agent.Workflow 状态](../../../Agent.Workflow.md)。

本文档提供 Agent.Engine 的服务级需求概览。详细实现需求请参阅各模块文档。

## 功能需求

### FR-1：聊天 API

| ID | 需求 | 端点 | 状态 |
|----|------|------|------|
| FR-1.1 | 同步聊天 | `POST /api/v1/agent/chat` | 已实现 |
| FR-1.2 | NDJSON 流式聊天 | `POST /api/v1/agent/chat/stream` | 已实现 |
| FR-1.3 | SSE 流式聊天 | `POST /api/v1/agent/chat/sse` | 已实现 |
| FR-1.4 | 原始 Pipeline 调用 | `POST /api/v1/agent/chat/pipeline` | 已实现 |
| FR-1.5 | 列出已发布 Agent | `GET /api/v1/agent/agents` | 已实现 |

> 详细 API 契约参见 [../modules/Host/ChatApi/03-DESIGN.md](../modules/Host/ChatApi/03-DESIGN.md)

### FR-2：服务注册与发现

| ID | 需求 | 实现 | 状态 |
|----|------|------|------|
| FR-2.1 | 启动时注册到 Redis | `RedisRegistry.RegisterAsync()` | 已实现 |
| FR-2.2 | 周期性心跳续约 | `HeartbeatService` (默认 10s) | 已实现 |
| FR-2.3 | 停机时注销 | `RedisRegistry.DeregisterAsync()` | 已实现 |
| FR-2.4 | 负载指标上报 | `GetCurrentLoad()` (内存/GC/线程池) | 已实现 |

> 详细实现参见 [../modules/Runtime/ServiceRegistration/03-DESIGN.md](../modules/Runtime/ServiceRegistration/03-DESIGN.md)

### FR-3：配置管理

| ID | 需求 | 实现 | 状态 |
|----|------|------|------|
| FR-3.1 | 从 Redis 加载 Agent 配置 | `ConfigProvider.GetConfigAsync()` | 已实现 |
| FR-3.2 | 内存快照缓存 | `ConfigSnapshot` (IMemoryCache) | 已实现 |
| FR-3.3 | 配置热加载 | `HotReloadService` (Redis Pub/Sub) | 已实现 |
| FR-3.4 | Mock 降级配置 | `CreateMockFallbackConfig()` (开发/测试环境) | 已实现 |

> 详细实现参见 [../modules/Runtime/ConfigHotReload/03-DESIGN.md](../modules/Runtime/ConfigHotReload/03-DESIGN.md)

### FR-4：启动时能力注册

| ID | 需求 | 实现 | 状态 |
|----|------|------|------|
| FR-4.1 | 从 Redis 加载 Skill 并注册到 IToolRegistry | `RedisSkillRegistrar` | 已实现 |
| FR-4.2 | 从 Redis 加载 RAG 实例并注册到 IRagRegistry | `RedisRagRegistrar` | 已实现 |
| FR-4.3 | 从 Redis 加载 LLM Profile 并注册到 ILlmRegistry | `RedisLlmRegistrar` | 已实现 |

### FR-5：优雅停机

| ID | 需求 | 实现 | 状态 |
|----|------|------|------|
| FR-5.1 | 跟踪在飞请求 | `ShutdownService` + `RequestScope` | 已实现 |
| FR-5.2 | 停机时拒绝新请求 | `IsShuttingDown` 检查 | 已实现 |
| FR-5.3 | 等待在飞请求完成 | `ShutdownAsync(timeout)` | 已实现 |
| FR-5.4 | 超时后强制退出 | 日志 Warning + 继续停机 | 已实现 |

## 非功能需求

### NFR-1：可用性

| ID | 需求 | 实现 |
|----|------|------|
| NFR-1.1 | Redis 不可用时继续服务（孤岛模式） | `IConnectionMultiplexer` 连接为 null 时各组件降级处理 |
| NFR-1.2 | Redis 自动重连 | `IConnectionMultiplexer` 自动重连（由 Agent.Core 注册） |
| NFR-1.3 | 心跳失败后自动重试注册 | `HeartbeatService` 循环重试 |

### NFR-2：可观测性

| ID | 需求 | 实现 |
|----|------|------|
| NFR-2.1 | OpenTelemetry Tracing | `Agent.Hosting` 集成，Source=`OpenAgent.Engine` |
| NFR-2.2 | OpenTelemetry Metrics | `Agent.Hosting` 集成 |
| NFR-2.3 | 结构化日志 | `ILogger<T>`，关键路径均有日志 |

> 详细实现参见 [../modules/Host/ErrorHandling/03-DESIGN.md](../modules/Host/ErrorHandling/03-DESIGN.md)

### NFR-3：安全性

| ID | 需求 | 实现 |
|----|------|------|
| NFR-3.1 | JWT Bearer 认证 | `Agent.Hosting` + `RequireAuthorization()` |
| NFR-3.2 | 用户上下文映射 | `ExtractUserContext()` → `IAgentUserContext` |
| NFR-3.3 | LLM API Key 安全存储 | 优先从环境变量读取 (`LLM__APIKEY` / `LLM_API_KEY`) |

### NFR-4：健康检查

| ID | 需求 | 实现 | Tag |
|----|------|------|-----|
| NFR-4.1 | Redis 连接检查 | `RedisHealthCheck` | infrastructure, ready, live |
| NFR-4.2 | 配置快照检查 | `ConfigHealthCheck` | ready |
| NFR-4.3 | LLM 连通性检查 | `LlmHealthCheck` | live |

> 详细实现参见 [../modules/Runtime/HealthCheck/03-DESIGN.md](../modules/Runtime/HealthCheck/03-DESIGN.md)

### NFR-5：错误处理

| ID | 需求 | 实现 |
|----|------|------|
| NFR-5.1 | 全局异常处理 (ProblemDetails) | `GlobalExceptionHandlerMiddleware` |
| NFR-5.2 | SSE 专用错误处理 | `SseErrorHandlerMiddleware` |
| NFR-5.3 | 流式错误恢复 | NDJSON/SSE 端点内 catch 块 |

## 配置项

| 配置路径 | 默认值 | 说明 |
|----------|--------|------|
| `ConnectionStrings:Redis` | `localhost:6379` | Redis 连接字符串 |
| `Engine:AllowMockAgent` | `true` (开发/测试), `false` (生产) | 是否允许 Mock Agent 降级 |
| `Heartbeat:IntervalSeconds` | 10 | 心跳间隔（秒） |
| `Heartbeat:RetryDelaySeconds` | 5 | 心跳失败重试延迟（秒） |
| `Heartbeat:RegistryTtlSeconds` | 30 | 注册条目 TTL（秒） |
| `Heartbeat:AdvertisedHost` | 空 (自动检测) | 对外广播的主机名 |
| `Heartbeat:AdvertisedPort` | null (自动检测) | 对外广播的端口 |
| `Shutdown:TimeoutSeconds` | 30 | 优雅停机等待超时（秒） |
| `Authentication:Authority` | `http://localhost:5003` | JWT Authority |
| `Authentication:Audience` | `agent-api` | JWT Audience |

## 不属于 Engine 范围的需求

| 需求 | 归属 |
|------|------|
| 流量路由与负载均衡 | Agent.Router |
| 工作流编排 | Agent.Workflow |
| 会话历史持久化 | Agent.Core |
| Agent 配置 CRUD | Agent.Matrix |
| Skill/RAG/LLM 配置管理 | Agent.Matrix |
