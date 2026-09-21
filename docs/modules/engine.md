# Engine — 运行时与 Host 适配

覆盖 Engine 服务的 MAF 运行时、配置管理、健康检查、停机、服务注册与 Host 层（Chat API、错误处理）。

## MAF 运行时

MAF（Microsoft Agent Framework）是 `OpenAgent.Core` 唯一生产运行时。运行时代码位于 `Backend/src/OpenAgent.Core/Runtime/Agent/`，随 Core 直接编译和注册。

能力：

- `ChatClientAgent.RunAsync` / `RunStreamingAsync`；
- `FunctionInvokingChatClient` 管理函数循环和最大迭代；
- 平台可用能力 JSON Schema 转为 `AIFunction`；
- `ChatHistoryProvider`、`AIContextProvider` 和 `CompactionProvider`；
- 原生历史消息、附件、usage 和流式 update；
- OpenAI Chat Completions、OpenAI Responses 和 Anthropic Messages；
- 通过 MAF provider 接入平台会话锁、存储、审计、指标及 NDJSON/SSE。

| MAF 负责 | 平台负责 |
|---|---|
| Agent 构造与运行 | 入口认证、租户和 Router |
| Provider `IChatClient` | 模型配置解析与授权 |
| 函数调用和结果回填 | 能力可用性解析、审计与外部执行治理 |
| 模型增量响应 | 会话锁、持久化和外部流协议 |

关键文件：

| 文件 | 职责 |
|---|---|
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentFactory.cs` | 创建 MAF Agent 与原生 provider |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentChatClientFactory.cs` | 模型 Provider |
| `Backend/src/OpenAgent.Core/Capabilities/CapabilityToolFactory.cs` | 可用能力到 `AIFunction` 的适配 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentResponseAdapter.cs` | MAF 响应和 usage 适配 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentMessageAdapter.cs` | 消息和文件资产内容 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs` | 原生执行与 turn 边界 |

规格约定：

- 每个 turn 使用授权后的 `LlmConfig` 创建 `IChatClient` 和 `ChatClientAgent`；`UseProvidedChatClientAsIs = true`。
- 每个 run 用 `FunctionInvokingChatClient` 包装 client；`MaximumIterationsPerRequest = AgentConfig.MaxTurns > 0 ? AgentConfig.MaxTurns : 5`。
- 未知函数终止运行；函数连续错误阈值为 3（`MaximumConsecutiveErrorsPerRequest = 3`）。
- 不可用能力在构建 Agent 前被过滤，不进入 MAF 工具列表；工具由携带原始 `ToolDefinition` 的 `AIFunction` 执行。
- 平台 PostgreSQL 会话是唯一持久历史；对象存储保存文件原始字节。
- `AddAgentCore` 是唯一 DI 注册入口。
- 支持的 `ApiFormat`：`OpenAIChatCompletions`、`OpenAIResponses`、`AnthropicMessages`。
- 消息适配支持 system/user/assistant/tool、function call/result、reasoning、文本文件和二进制 `DataContent`。

设计与执行流：身份和模型授权后立即创建 `ChatClientAgent` 与 `AgentSession`（不再构造 `ExecutionContext` 或 Engine 请求）：

```text
AgentExecutor
  -> IdentityResolution
  -> AgentFactory -> ChatClientAgent
       -> PlatformChatHistory : ChatHistoryProvider
       -> CapabilityToolFactory : AITool
       -> CompactionProvider
       -> FunctionInvokingChatClient
  -> Agent.Run[Streaming]Async
```

`PlatformChatHistory` 在 MAF 请求历史时加载 PostgreSQL 消息，并在 MAF 结束通知中写回成功、失败或取消状态。`CapabilityToolFactory` 发现并筛选可用能力，直接提供携带执行体的 `AIFunction`；工具名称、描述与 schema 不复制到 system prompt。Provider 由 `AgentChatClientFactory` 构造 `IChatClient`；新增能力只产生 `AIFunction`；新增记忆实现只扩展 `ChatHistoryProvider`；多 Agent 编排只使用 MAF Workflow。

设计约定：

- 新生产能力只扩展 MAF，不再增加并行 Agent 引擎。
- `SemanticKernel`、`LangChain`、`OpenAIDriver` 只作为配置兼容值，运行时全部归一为 `MAF`。
- Provider 协议必须使用对应 SDK；Responses、Anthropic 不伪装成 Chat Completions。
- `FunctionInvokingChatClient` 是工具循环唯一 owner；平台不得实现第二个模型轮次循环。
- 工具声明必须保留注册表 JSON Schema，不用反射推断替代。
- 平台会话库是唯一永久数据主责；MAF run 接收平台已加载的历史；Agent 不缓存，以配置热更新的正确性优先。

附件约定：

- 图片和 PDF 以 `DataContent` 发送；文本格式严格按 UTF-8 解码。
- 文件名使用 `Path.GetFileName`，禁止路径穿越；同时校验允许的扩展名、媒体类型及二者匹配关系。
- 附件原始字节不写日志、不写会话正文、不接受普通 JSON 绑定。
- Provider/模型不支持某媒体类型时，保留其明确上游错误，不伪造解析结果。

权限约定：

- `use` 是能力是否可用的唯一判断；不可用能力不会暴露给模型。
- Tool、Function、来源资源和父级资源共同决定最终可用性，不拆分为 discover/execute 两个动作。
- 策略实现通过 `IAgentAuthorizationService` 注入；默认 allow-all 只保证升级兼容。
- 具体能力执行层负责 Provider 的连接、不可用和执行错误；MAF 只负责工具循环。

版本约定：

- MAF 稳定包保持同一版本线；Anthropic 预览包必须与稳定 MAF 版本匹配并独立回归。
- 任何 Provider SDK 升级都至少运行工厂、消息、函数循环与流式测试。

后续可选增强：Host 前增加 magic-byte 检测、病毒扫描和租户对象存储；对真实生产凭据执行各 Provider 长期合约回归；Anthropic MAF 集成稳定后移除预览包；业务启用多 Agent 时再评估 MAF Workflow（当前多 Agent 编排未启用）。

测试围绕 MAF 原生边界：fake `IChatClient` 驱动 `ChatClientAgent`、`AIContextProvider` 能力发现与授权执行、`ChatHistoryProvider` 锁与写回、`CompactionProvider` 消息组压缩、`AgentSession` 工具循环与流式响应。生产 Core 与 Engine Host 保持 0 warning 编译；真实 Provider、Redis、MCP E2E 单独验证。

## 配置管理

Agent 和 LLM 配置通过独立的 `ConfigurationController` 暴露，保留 `/api/v1/admin/agents` 和 `/api/v1/admin/llm` 路径；宿主目前只在 Development 环境映射管理端点。

```text
HTTP + authenticated tenant
  -> ConfigurationController
  -> ConfigurationService
  -> IAgentConfigRepository / ILlmConfigRepository
  -> EF Core -> PostgreSQL commit
  -> Redis TTL cache refresh
  -> redacted HTTP response
```

Controller 负责路由、HTTP 验证、Skill 绑定可用性检查、密钥响应脱敏及连接测试。`ConfigurationService` 合并原 Agent 管理服务、运行时 Provider、数据库缓存包装和 LLM 管理服务，负责租户归属、密钥保留、Repository 调用和缓存。运行时继续通过 Contracts 中的 `IAgentConfigProvider` / `ILlmConfigProvider` 读取配置；DI 把两个接口指向同一个 `ConfigurationService`，Core 无须引用 Engine 或 EF Core。

读取、更新和缓存：

- 管理列表和 Agent 配置详情直接读 PostgreSQL；运行时 Agent/LLM 读取和 LLM 详情先读 Redis，未命中或缓存不可用时回源并回填。
- 保存先提交 PostgreSQL，再覆盖对应租户和资源的 Redis 项。Agent 使用版本号检查并发更新；LLM 当前没有乐观并发版本。删除 LLM 先删数据库记录，再删缓存。
- TTL 由 `ConfigurationStore:RedisCacheTtlSeconds` 控制，默认 300 秒；没有周期全量预热、缓存索引、Snapshot、Pub/Sub 或 Mock 配置源。
- Key 使用 `agent:config-cache:v2:{tenant}:{agent}`、`llm:config-cache:v2:{tenant}:{profile}`，租户和资源 ID 分别转义；读取命中后再核对缓存内容中的租户和资源 ID。
- PostgreSQL 与 Redis 不是同一事务：缓存写入/删除失败不会回滚数据库；旧缓存可能保留到 TTL 到期，本范围没有 outbox。

执行与功能隔离：

```text
ChatRequest(agentId, llmProfileId, fileIds)
  -> AgentExecutor -> AgentRuntimeResolver
      -> read Agent and LLM independently
      -> Agent / Model authorization
      -> request-scoped AgentRuntimeProfile
  -> FileAssetRequestResolver
  -> AgentFactory -> model client + history + tools
  -> AIAgent.Run[Streaming]Async
```

Agent 保存指令、轮次、上下文压缩策略及能力绑定；LLM 保存模型连接、ContextTokens、Modality 和加密密钥；执行时才组合两者，LLM 的 ContextTokens 决定有效上下文上限。图片上传继续使用现有 FileAssetService、PostgreSQL 文件元数据和对象存储；Multimodal 仅控制图片二进制的受限读取与内联，音频、视频输入暂不开放。MCP/RAG/Skill 管理接口留在已有能力模块；会话、文件、服务发现和分布式锁使用各自契约与服务，配置服务不接管这些功能。

密钥：LLM 和 RAG API Key 使用租户绑定的服务端加密值存储在数据库和 Redis 缓存；GET/PUT 响应通过 `ConfigurationRedactor` 清空 Key；编辑时空 Key 或掩码保留数据库值，连接测试按已认证租户和 Profile ID 补齐并解密保存的 Key。RAG 仍兼容旧的 `ApiKeySecretRef`。

源码：HTTP `Backend/src/OpenAgent.Engine.Host/Controllers/ConfigurationController.cs`；配置服务与缓存 `Backend/src/OpenAgent.Engine/Config/ConfigurationService.cs`；数据访问 `Backend/src/OpenAgent.Infrastructure/Configuration/`；运行时组合 `Backend/src/OpenAgent.Core/Runtime/Agent/AgentRuntimeResolver.cs`。表结构见 [database.md](../database.md)。

## 配置热更新

配置热更新不使用 Redis Pub/Sub、进程内 Snapshot 或 LLM Registry。管理端更新先提交 PostgreSQL，再立即覆盖当前租户和资源对应的 Redis TTL 缓存；其他实例不依赖通知——缓存到期或未命中时直接从 PostgreSQL 回源。只有一个事实源，不需要维护消息兼容、重放和本地快照失效逻辑。一次执行在开始时解析 Agent 与所选 LLM Profile，并在该次执行内保持不变；后续请求读取最新可见配置。

## 健康检查

三类健康检查覆盖 Redis 连接、Agent 配置缓存和 LLM 配置可读性，通过 ASP.NET Core 健康检查框架注册，暴露 `/health`（live）和 `/ready`（ready）端点。

| Check | Tags | Endpoint |
|-------|------|----------|
| redis | infrastructure, ready, live | /health + /ready |
| agent-config | ready | /ready |
| llm-connectivity | live | /health |

- 状态分 Healthy / Degraded / Unhealthy 三级；标签分组支持不同探针。
- 仅验证配置可读性，不发起真实外部 API 调用；`LlmHealthCheck` 仅检查第一个 Agent 作为样本。
- Redis 不可用返回 Degraded（非 Unhealthy），Engine 可在孤岛模式运行。

源码：`Backend/src/OpenAgent.Engine/Redis/RedisHealthCheck.cs`、`ConfigHealthCheck.cs`、`LlmHealthCheck.cs`；注册 `Backend/src/OpenAgent.Engine/Extensions/ServiceCollectionExtensions.cs`；测试 `Backend/tests/OpenAgent.Engine.Tests/HealthChecks/`。

## 优雅停机

GracefulShutdown 跟踪进行中的请求，确保 Engine 停机时不中断正在处理的请求。

```text
ApplicationStopping
  -> ShutdownService.ShutdownAsync(timeout)
  -> RedisRegistry.DeregisterAsync()

请求入口 -> RequestScope(RegisterRequest) -> 执行 -> Dispose(CompleteRequest)
```

- `ConcurrentDictionary` 跟踪所有进行中请求；`RequestScope`（`IDisposable`）自动注册/完成请求。
- 停机时新请求抛出 `AgentException(DependencyUnavailable)`；轮询等待进行中请求完成。
- 超时（`Shutdown:TimeoutSeconds`，默认 30 秒）后仅记录 Warning，不强制终止请求；停机顺序依赖 `Program.cs` 编排。
- 状态：核心功能已实现，缺少专门单元测试。

源码：`Backend/src/OpenAgent.Engine/Runtime/ShutdownService.cs`、`RequestScope.cs`；编排 `Backend/src/OpenAgent.Engine.Host/Program.cs`。

## 服务注册

每个 Engine 实例启动后生成唯一 EngineId，将自身信息写入 Redis 并周期性发送心跳，停机时主动注销。

```text
HeartbeatService (BackgroundService)
  -> RedisRegistry.RegisterAsync / HeartbeatAsync / DeregisterAsync
  -> Redis String (engine:registry:{engineId}, TTL=30s)
  -> Redis Set (engine:registry:index)
```

- 自动注册写入实例信息并维护发现索引；心跳周期性更新注册信息（含负载指标）并刷新 TTL；停机时主动注销。
- 孤岛模式：Redis 不可用时降级运行，不阻塞 Engine 启动。
- 负载上报综合内存、GC、线程池压力计算负载值；路由元数据随心跳发布支持的 intent。
- 已知限制：心跳单线程顺序执行，`IsRegistered` 非 volatile；注销失败依赖 TTL 自然过期；进程异常退出会暂时留下索引成员，Router 发现注册值已过期后会清理。

源码：接口 `Backend/src/OpenAgent.Engine/Abstractions/IEngineRegistry.cs`；实现 `Backend/src/OpenAgent.Engine/Registry/RedisRegistry.cs`、`Runtime/HeartbeatService.cs`；模型 `Backend/src/OpenAgent.Engine/Models/HeartbeatOptions.cs`、`RegistryEntry.cs`；测试 `Backend/tests/OpenAgent.Engine.Tests/Registry/RedisRegistryTests.cs`。Router 侧消费见 [router.md](./router.md)。

## 能力注册

LLM 不通过启动期 Registrar 或内存 Registry 注册，而是每次执行时按租户和 `llmProfileId` 从 PostgreSQL/Redis TTL 缓存解析。RAG 与 MCP 仍使用现有目录边界；MCP 和 Skill 按 Agent 保存的 ID 在执行时创建官方 SDK 资源。

```text
AgentFactory
  ├─ ILlmConfigProvider          -> selected model profile
  ├─ McpToolFactory              -> official McpClientTool
  └─ AgentSkillsProviderFactory  -> official AgentSkillsProvider
```

LLM Profile 的租户隔离由持久化主键、Redis key 和执行时的已验证 tenantId 三层共同约束。

## Chat API（Host 层）

ChatApi 提供 Engine 的核心 HTTP 端点；聊天使用 JSON，文件须先独立上传并以 `fileIds` 引用。

| 端点 | 方法 | 说明 | 响应类型 |
|------|------|------|----------|
| `/api/v1/agent/chat` | POST | 同步聊天 | JSON |
| `/api/v1/agent/chat/stream` | POST | SSE 流式 | text/event-stream |
| `/api/v1/agent/files` | POST | 独立上传文件资产 | JSON |
| `/api/v1/agent/files/{fileId}/content` | GET | 文件预览内容 | 原始 MIME |
| `/api/v1/agent/files/{fileId}/download` | GET | 下载文件资产 | 原始 MIME |
| `/api/v1/agent/agents` | GET | Agent 列表 | JSON |
| `/api/v1/agent/conversations` | GET | 会话列表 | JSON |
| `/api/v1/agent/conversations/search` | GET | 会话搜索 | JSON |
| `/api/v1/agent/conversations/{conversationId}` | GET | 会话详情 | JSON |
| `/api/v1/agent/conversations/{conversationId}` | DELETE | 软删除会话 | 空响应 |
| `/health`、`/health/live` | GET | 存活检查 | Health Check |
| `/ready`、`/health/ready` | GET | 就绪检查 | Health Check |
| `/metrics` | GET | Prometheus 指标 | Text |

核心能力：

- 流式 SSE 事件流；客户端断开时正确释放资源（优雅中断）。
- 多模态输入：聊天以 `fileIds` 引用已上传文件，执行时按需读取，不在会话中保存字节。
- MCP 跨系统传输 / 用户分享链接：模型调用 `create_file_transfer_url` 返回平台分享链接 `/api/v1/share/{token}`（`audience` 决定默认策略：mcp 2 小时/2 次下载、user 3 天/不限次；可选 `mode`：temporary/singleUse/longTerm 显式覆盖，`expiresInSeconds` 自定义失效日期，全量上限 365 天、不存在永久链接）——既可传给需要文件 URL 的第三方 MCP，也可作为下载链接交给用户（须告知 `expiresAt` 有效期与下载限制）；链接不暴露 S3 地址、`objectKey` 或对象存储凭据。
- 分享链接管理：`GET /api/v1/agent/files/shares` 查询当前用户仍有效的分享链接（已过期/已用尽/已撤销的不返回，按创建时间倒序），`DELETE /api/v1/agent/files/shares/{shareId}` 撤销指定分享（软删除：标记失效、令牌立即 404）。查询与撤销仅保留 REST 端点，模型侧不暴露分享管理工具。
- 上传防护：数量、大小、MIME 类型校验；请求追踪：Header / Activity 自动生成 TraceId。

Token usage 契约：非流式 `/chat` 响应在 `message` 外增加可选 `usage` 和 `modelId`。流式 `/chat/stream` 不发送独立 usage 事件，而是在终态事件中返回：

```text
event: done
data: {"done":true,"usage":{"promptTokens":21,"completionTokens":8,"totalTokens":29},"modelId":"provider-model","conversationId":"..."}
```

`usage` 为 `null` 表示 Provider 未返回完整统计，客户端不得将其解释为 0。可选的 `cachedInputTokens`、`reasoningTokens` 是细分项，不额外计入 total。旧客户端可忽略新增字段，原有 `message`、content/reasoning/tool_call/done 事件名称保持不变。

源码：端点组合 `Backend/src/OpenAgent.Engine.Host/Extensions/EndpointExtensions.cs`；聊天 `AgentChatEndpointExtensions.cs`；会话 `ConversationEndpointExtensions.cs`；文件 `FileAssetEndpointExtensions.cs`；流式 `AgentStreamWriter.cs`、`StreamingPayloadFactory.cs`；中间件 `Middleware/`。

## Host 错误处理

全局异常处理确保所有未处理异常都被转换为结构化错误响应。

```text
HTTP 请求
  -> AgentExceptionHandlerMiddleware（SSE 异常->error/done；其他异常->ProblemDetails）
  -> Endpoint Handlers
```

异常映射：

| Exception | HTTP |
|-----------|------|
| UnauthorizedAccessException | 403 |
| HumanApprovalRequiredException | 202 |
| AgentException | 按 ErrorCode 分组映射（403/404/409/429/400/503/500） |
| TimeoutException | 504 |
| ClientResultException | 4xx 或 502（Provider 故障） |
| HttpRequestException | 401/403/404/429/503（Provider HTTP 故障） |
| 其他 | 500 |

限制：响应已开始时重新抛出异常，不写入 ProblemDetails。

源码：`Backend/src/OpenAgent.Engine.Host/Middleware/AgentExceptionHandlerMiddleware.cs`；载荷 `StreamingPayloadFactory.cs`；编排 `Program.cs`；测试 `Backend/tests/OpenAgent.Engine.Tests/Hosting/AgentExceptionHandlerMiddlewareTests.cs`。
