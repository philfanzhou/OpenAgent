# 集成矩阵

## 外部交互矩阵

| # | 交互对象 | 类型 | 方向 | 协议/方式 | 触发条件 | 代码位置 |
|---|----------|------|------|-----------|----------|----------|
| 1 | Redis - 服务注册 | 数据 | 写 | `StringSetAsync` / `KeyDeleteAsync` | 启动注册、心跳续约、停机注销 | `RedisRegistry` |
| 2 | Redis - 心跳续约 | 数据 | 写 | `StringSetAsync` (带 TTL) | 每 `IntervalSeconds` 秒 | `HeartbeatService` |
| 3 | Redis - 配置读取 | 数据 | 读 | `StringGetAsync` / `SetMembersAsync` | 请求到达时按需加载 | `ConfigProvider` |
| 4 | Redis - 配置热加载 | 消息 | 订阅 | Pub/Sub | 收到配置变更通知 | `HotReloadService` |
| 5 | Redis - Skill 注册 | 数据 | 读 | `SetMembersAsync` / `StringGet` | 启动时一次性加载 | `RedisSkillRegistrar` |
| 6 | Redis - RAG 注册 | 数据 | 读 | `SetMembersAsync` / `StringGet` | 启动时一次性加载 | `RedisRagRegistrar` |
| 7 | Redis - LLM 注册 | 数据 | 读 | `SetMembersAsync` / `StringGet` | 启动时一次性加载 | `RedisLlmRegistrar` |
| 8 | Redis - 健康检查 | 数据 | 读 | `PingAsync` / `SetMembersAsync` | `/health` / `/ready` 探测 | `RedisHealthCheck` / `ConfigHealthCheck` |
| 9 | Agent.Core Pipeline | 进程内 | 调用 | `IAgentPipeline.ExecuteAsync` / `ExecuteStreamAsync` | 每次聊天请求 | `EndpointExtensions` |
| 10 | Agent.Hosting JWT | 进程内 | 调用 | `AddAgentHost` / `UseAgentHost` | 应用启动 | `Program.cs` |
| 11 | Agent.Hosting OpenTelemetry | 进程内 | 调用 | `AddAgentHost` (配置 `OpenTelemetrySource`) | 应用启动 | `Program.cs` |
| 12 | 外部 LLM API | 网络 | 出站 | HTTP | Core Pipeline 执行推理 | Agent.Core 引擎实现 |
| 13 | 外部 RAG 服务 | 网络 | 出站 | HTTP | Core Pipeline 执行 RAG 检索 | Agent.Core RAG 实现 |
| 14 | 外部 MCP 服务器 | 网络 | 出站 | HTTP/SSE | Core Pipeline 执行 MCP 工具调用 | Agent.Core MCP 实现 |
| 15 | Skill HTTP 端点 | 网络 | 出站 | HTTP POST | `RedisMockSkill.ExecuteAsync` | `RedisSkillRegistrar.RedisMockSkill` |

## Redis Key 交互明细

| Redis Key | 操作 | 方向 | 使用者 | 说明 |
|-----------|------|------|--------|------|
| `engine:registry:{engineId}` | SET / DEL | 写 | `RedisRegistry` | Engine 自身注册条目（带 TTL） |
| `agent:config:{agentId}` | GET | 读 | `ConfigProvider`, `HotReloadService` | Agent 配置 JSON |
| `agent:published:index` | SMEMBERS | 读 | `ConfigProvider`, `ConfigHealthCheck`, `LlmHealthCheck` | 已发布 Agent ID 集合 |
| `skill:registry:{skillName}` | GET | 读 | `RedisSkillRegistrar` | Skill 定义 JSON |
| `skill:published:index` | SMEMBERS | 读 | `RedisSkillRegistrar` | 已发布 Skill 名称集合 |
| `llm:registry:{profileId}` | GET | 读 | `RedisLlmRegistrar` | LLM Profile JSON |
| `llm:published:index` | SMEMBERS | 读 | `RedisLlmRegistrar` | 已发布 LLM Profile 集合 |
| `rag:registry:{instanceId}` | GET | 读 | `RedisRagRegistrar` | RAG 实例 JSON |
| `rag:published:index` | SMEMBERS | 读 | `RedisRagRegistrar` | 已发布 RAG 实例集合 |

## Redis Pub/Sub 通道

| 通道名 | 类型 | 使用者 | 说明 |
|--------|------|--------|------|
| `agent:config:updates` | 当前结构化通道 | `HotReloadService` | 携带 JSON 格式的 `ConfigUpdate` 消息 |
| `agent:config:changed` | 遗留通道 | `HotReloadService` | 仅携带 agentId 字符串 |
| `skill:registry:changed` | 遗留通道 | `HotReloadService` | Skill 注册变更通知 |
| `llm:registry:changed` | 遗留通道 | `HotReloadService` | LLM 注册变更通知 |
| `rag:registry:changed` | 遗留通道 | `HotReloadService` | RAG 注册变更通知 |
| `engine:config:changed` | 遗留通道 | `HotReloadService` | Engine 配置变更通知 |

## 故障语义

### Redis 不可用

| 影响范围 | 降级行为 | 恢复方式 |
|----------|----------|----------|
| 服务注册 | 注册失败，`IsRegistered = false`，日志 Warning，继续运行（孤岛模式） | `IConnectionMultiplexer` 自动重连，`HeartbeatService` 循环重试注册 |
| 心跳续约 | 心跳失败，`IsRegistered = false`，日志 Warning | 同上，下次心跳周期重试 |
| 配置读取 | 跳过 Redis 查询，仅使用内存快照；快照为空时降级为 Mock（若允许） | Redis 恢复后下次请求自动从 Redis 加载 |
| 配置热加载 | Pub/Sub 断连，不再收到更新通知 | `IConnectionMultiplexer` 重连后 Pub/Sub 自动恢复 [待确认] |
| Skill/RAG/LLM 注册 | 启动时跳过，日志 Debug | 需重启 Engine 才能重新加载 |
| 健康检查 | `RedisHealthCheck` → Degraded；`ConfigHealthCheck` → Degraded | Redis 恢复后下次探测自动 Healthy |

### 外部 LLM API 不可用

| 影响范围 | 降级行为 |
|----------|----------|
| 聊天请求 | Core Pipeline 抛出异常 → `GlobalExceptionHandlerMiddleware` 返回 504/500 |
| LLM 健康检查 | `LlmHealthCheck` → Degraded（无法获取 LLM 配置时） |

### Skill HTTP 端点不可用

| 影响范围 | 降级行为 |
|----------|----------|
| Skill 调用 | `RedisMockSkill.ExecuteAsync` 捕获异常，返回错误消息字符串（不抛出） |

### 优雅停机

| 阶段 | 行为 |
|------|------|
| 收到停止信号 | `ShutdownService.IsShuttingDown = true`，拒绝新请求（抛出 `AgentException`） |
| 等待在飞请求 | 轮询 `_inFlightRequests`，最长等待 `Shutdown:TimeoutSeconds`（默认 30 秒） |
| 超时后 | 日志 Warning，记录仍在运行的请求 |
| 注销 | `RedisRegistry.DeregisterAsync()` 删除 `engine:registry:{engineId}`；失败则依赖 TTL 自然过期 |
