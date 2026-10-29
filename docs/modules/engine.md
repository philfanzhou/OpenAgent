# Engine — 运行时驱动、Host 与运行时管理

本域包含 OpenAgent.Engine 的配置、注册、运行时管理与共享 Hosting，以及 OpenAgent.Engine.Host 的 HTTP 宿主文档。聊天推理由运行时驱动（`IAgentRuntimeDriver`）执行，平台不再内置 Agent 推理循环；Microsoft Agent Framework（MAF）已在 PR #234 移除，实现边界见 [OpenCode 运行时](../integrations/opencode-runtime.md)，服务与模块归属见 [开发指南](../overview/DevelopmentGuide.md)。

## 运行时驱动与平台执行协调

SDK 与平台边界：

| 驱动管理 | 平台管理 |
|---|---|
| harness 进程与 ACP 会话、消息与工具的协议往返 | 身份、模型配置、轮次与资源生命周期 |
| 沙箱隔离（harness 自带机制） | 工具发现、权限、结果预算、超时与外部执行 |
| 外部 MCP server 的原生连接 | MCP 配置目录与 Agent 绑定关系 |
| skill 包的原生发现 | 平台会话历史、持久化与压缩策略 |

平台不复制推理协议或工具执行逻辑，也不存在第二条执行路径。PostgreSQL 是会话与文件元数据的事实源；对象存储保存文件字节。

运行与接入：

- 端点直接注入 `RuntimeAgentExecutor` 作为稳定入口：解析运行配置、准备历史与工具、调用驱动、落库事件。
- `RuntimeInvocation` 携带本轮的全部输入（配置、轮次上下文、指令、消息、工具、skill、MCP server）；驱动只看到已授权的输入，不暴露协议类型。
- 平台工具经 `CapabilityToolFactory` 筛选并转换为驱动可调用的工具；`RuntimeTool.InvokeAsync` 绑定本轮身份，Agent 不能在参数中更换租户或用户。
- 工具独占并发门由 `RuntimePreparationScope` 持有，超时与结果预算由 `IsolatedToolFunction` 包装统一执行；MCP/Skill 通过 `IAgentFeatureFactory` 贡献定义与待释放资源，Execution 不引用具体能力。
- 平台会话由 `PlatformChatHistory` 加载与写回；自动压缩受 `ConversationStore:EnableAutoCompaction` 控制，默认关闭。

Provider 与内容：支持 ApiFormat OpenAIChatCompletions、OpenAIResponses、AnthropicMessages，使用对应 SDK；OpenAI 兼容服务通过其协议与 Endpoint 配置接入，不伪装不同协议。模型工厂还负责出站规格化、交互记录与压缩客户端。Mapping 处理 system/user/assistant/tool、函数调用/结果、reasoning、文本及 DataContent；图片/PDF 按二进制内容发送，文本严格按 UTF-8 解码；文件名、扩展名、媒体类型与所有权检查在文件入口执行，不支持的媒体由 Provider 明确报错。交互捕获与导出边界见 [观测文档](../overview/Observability.md)；工具 schema、错误与隔离规则见 [能力文档](capabilities.md)，附件与共享策略见 [文件集成](../integrations/README.md)。

能力解析：LLM 不再通过启动期 Registrar 或内存 Registry 注册，而是在每次执行时按租户和 `llmProfileId` 从 PostgreSQL/Redis TTL 缓存解析；Agent、Runtime 与 MCP 配置同样以 PostgreSQL 为事实源（RAG 目录仍在 Engine/Redis 的 `RedisRagRegistrar`）。外部 MCP 与 Skill 不创建官方 SDK 客户端资源，分别经 ACP 透传与工作区物化交给 harness 原生处理。

扩展约束：新能力扩展现有驱动/平台接口，不新增并行 Agent 引擎或第二个 composition root；真实模型与 MCP 按环境单独验收，不由单元测试推断生产可用性。

| 路径（Backend/src/OpenAgent.Application/ 下） | 职责 |
|---|---|
| Execution/RuntimeAgentExecutor.cs | 端点稳定入口与平台执行协调 |
| Execution/RuntimePreparation.cs、RuntimePreparationScope.cs | 本轮历史、工具、特性与资源准备 |
| ModelProviders/AgentChatClientFactory.cs | Provider 与交互记录装饰 |
| Tooling/Discovery/CapabilityToolFactory.cs | 平台工具适配与授权筛选 |
| Capabilities/Mcp/McpFeatureFactory.cs、Capabilities/Skill/SkillFeatureFactory.cs | 能力定义贡献 |
| Conversation/History/PlatformChatHistory.cs | 平台历史 |
| Mapping/AgentMessageAdapter.cs、AgentResponseAdapter.cs | 内容、usage 与流式转换 |

## Host HTTP 端点

ChatApi 由 OpenAgent.Engine.Host 提供核心 HTTP 端点；聊天使用 JSON，文件须先独立上传并以 `fileIds` 引用。

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

全部端点为 Minimal API + `TypedResults`，响应为具名 DTO；错误统一为 ProblemDetails（见 [API 规范](../overview/API.md)），开发环境 `/swagger` 提供完整 OpenAPI 文档。

文件分享链接：大模型调用 `create_file_transfer_url` 返回平台分享链接 `/api/v1/share/{token}`（`audience` 决定默认策略：mcp 2 小时/2 次下载、user 3 天/不限次；可选 `mode`：temporary/singleUse/longTerm 显式覆盖，`expiresInSeconds` 自定义失效日期，全量上限 365 天、不存在永久链接）——既可传给需要文件 URL 的第三方 MCP，也可作为下载链接交给用户（须告知 `expiresAt` 有效期与下载限制）；链接不暴露 S3 地址、`objectKey` 或对象存储凭据。`GET /api/v1/agent/files/shares` 查询当前用户仍有效的分享链接（按创建时间倒序），`DELETE /api/v1/agent/files/shares/{shareId}` 撤销（软删除：标记失效、令牌立即 404）；模型侧不暴露分享管理工具，避免模型误传 shareId 空参数。

Token usage 契约：非流式 `/chat` 响应在 `message` 外增加可选 `usage` 和 `modelId`；流式 `/chat/stream` 不发送独立 usage 事件，而是在终态事件返回：

```text
event: done
data: {"done":true,"usage":{"promptTokens":21,"completionTokens":8,"totalTokens":29},"modelId":"provider-model","conversationId":"..."}
```

`usage` 为 `null` 表示 Provider 未返回完整统计，客户端不得将其解释为 0；可选的 `cachedInputTokens`、`reasoningTokens` 是细分项，不额外计入 total。旧客户端可忽略新增字段，原有事件名称保持不变。

其他能力：多模态输入（聊天以 `fileIds` 引用已上传文件，执行时按需读取，不在会话中保存字节）、上传防护（数量、大小、MIME 类型校验）、请求追踪（Header/Activity 自动生成 TraceId）、优雅中断（客户端断开时正确释放资源）。

## 全局错误处理

全平台共享的全局异常处理确保所有未处理异常都被转换为结构化错误响应。核心设施位于 `OpenAgent.Engine/Hosting/Errors`，Engine/Router 共用；Engine 通过选项注入自己的 SDK 异常映射与中文 SSE 文案。

```text
HTTP 请求
  → AgentExceptionHandlerMiddleware（Hosting 共享；SSE 异常→error/done；其他异常→ProblemDetails）
    ├─ AgentExceptionMapper（核心映射 + Engine 注册的 ClientResultException 扩展映射）
    └─ AgentProblemDetails（统一载荷构造）
  → Endpoint Handlers
```

| 异常 | HTTP |
|-----------|------|
| UnauthorizedAccessException | 403 |
| HumanApprovalRequiredException | 202（含 approvalToken/actionDescription 扩展）|
| AgentException | 按 ErrorCode（401/403/404/409/429/400/503/500 分组映射）|
| TimeoutException | 504 |
| ClientResultException（Engine 扩展映射） | 4xx 或 502（Provider 故障）|
| HttpRequestException | 401/403/404/429/503（Provider HTTP 故障）|
| 其他 | 500 |

统一错误契约扩展字段 `traceId`/`timestamp`/`errorCode`（见 [API 规范](../overview/API.md)）。响应已开始时重新抛出异常，不写入 ProblemDetails（仅流式端点发 error/done 事件）。

## 运行时管理

### 配置管理与热更新

Agent 和 LLM 配置经 `ConfigurationEndpointExtensions` 汇总门面映射为 Minimal API（Agent/LLM/MCP 子域分别位于 `Endpoints/Admin/{Agents,Models,Mcp}`，RAG 端点暂留门面文件），保留 `/api/v1/admin/agents` 和 `/api/v1/admin/llm` 路径；宿主只在 Development 环境映射管理端点。端点层负责路由、HTTP 验证、Skill 绑定可用性检查、密钥响应脱敏及连接测试；ConfigurationService 合并原 Agent 管理服务、运行时 Provider、数据库缓存包装和 LLM 管理服务，负责租户归属、密钥保留、Repository 调用和缓存。运行时继续通过 Contracts 中的 IAgentConfigProvider / ILlmConfigProvider 读取配置；DI 把两个接口指向同一个 ConfigurationService，Application 无须引用 Engine 或 EF Core。

```text
HTTP + authenticated tenant
  -> ConfigurationEndpointExtensions
  -> ConfigurationService
  -> IAgentConfigRepository / ILlmConfigRepository
  -> EF Core -> PostgreSQL commit
  -> Redis TTL cache refresh
  -> redacted HTTP response
```

读取、更新和缓存（配置热更新不使用 Redis Pub/Sub、进程内 Snapshot 或 LLM Registry；其他实例不依赖通知，缓存到期或未命中时直接从 PostgreSQL 回源，只有一个事实源）：

- 管理列表和 Agent 配置详情直接读 PostgreSQL；运行时 Agent/LLM 读取和 LLM 详情先读 Redis，未命中或缓存不可用时回源并回填。
- 保存先提交 PostgreSQL，再覆盖对应租户和资源的 Redis 项。Agent 使用版本号检查并发更新；LLM 当前没有乐观并发版本。删除 LLM 先删数据库记录，再删缓存。
- TTL 由 `ConfigurationStore:RedisCacheTtlSeconds` 控制，默认 300 秒；没有周期全量预热、缓存索引、Snapshot、Pub/Sub 或 Mock 配置源。
- Key 使用 `agent:config-cache:v2:{tenant}:{agent}`、`llm:config-cache:v2:{tenant}:{profile}`，租户和资源 ID 分别转义；读取命中后再核对缓存内容中的租户和资源 ID。
- PostgreSQL 与 Redis 不是同一事务：缓存写入/删除失败不回滚数据库；旧缓存可能保留到 TTL 到期，没有 outbox。

执行期组合：一次执行在开始时解析 Agent 与所选 LLM Profile，并在该次执行内保持不变；后续请求读取最新可见配置。

```text
ChatRequest(agentId, llmProfileId, fileIds)
  -> RuntimeAgentExecutor
  -> AgentRuntimeResolver
      -> read Agent and LLM independently
      -> Agent / Model authorization
      -> request-scoped AgentRuntimeProfile
  -> FileAssetRequestResolver
  -> RuntimePreparation -> history + tools + MCP/Skill
  -> IAgentRuntimeDriver（OpenCode ACP）
```

Agent 保存指令、轮次、上下文压缩策略及能力绑定；LLM 保存模型连接、ContextTokens、Modality 和加密密钥；执行时才组合两者，LLM 的 ContextTokens 决定有效上下文上限。图片上传继续使用现有 FileAssetService、PostgreSQL 文件元数据和对象存储；Multimodal 仅控制图片二进制的受限读取与内联，不新增配置与文件存储的依赖；音频、视频输入暂不开放。MCP/RAG/Skill 管理接口留在已有能力模块；会话、文件、服务发现和分布式锁使用各自契约与表，配置服务不会接管这些功能。

密钥：LLM 和 RAG API Key 使用租户绑定的服务端加密值存储在数据库和 Redis 缓存；GET/PUT 响应通过 ConfigurationRedactor 清空 Key；编辑时空 Key 或掩码保留数据库值，连接测试按已认证租户和 Profile ID 补齐并解密保存的 Key。RAG 仍兼容旧的 ApiKeySecretRef。表结构见 [配置表](../database/tables/Configurations.md)。

| 层 | 位置 |
|---|---|
| HTTP | Backend/src/OpenAgent.Engine.Host/Extensions/ConfigurationEndpointExtensions.cs |
| 配置服务与缓存 | Backend/src/OpenAgent.Engine/Config/ConfigurationService.cs |
| 数据访问 | Backend/src/OpenAgent.Framework/Configuration/ |
| 运行时组合 | Backend/src/OpenAgent.Application/Execution/AgentRuntimeResolver.cs |

### 健康检查

通过 ASP.NET Core 健康检查框架注册三类检查，暴露 `/health`（live）和 `/ready`（ready）：

| Check | 内容 | Tags |
|-------|------|------|
| redis | Redis 可用性和 Ping 响应 | infrastructure, ready, live |
| agent-config | 已发布 Agent 的配置缓存完整度 | ready |
| llm-connectivity | 示例 Agent 的 LLM 配置可读性（不发起真实 API 调用，仅检查第一个 Agent 作为样本）| live |

状态分级 Healthy / Degraded / Unhealthy；Redis 不可用返回 Degraded（非 Unhealthy），Engine 可在孤岛模式运行。

### 优雅停机

GracefulShutdown 跟踪进行中的请求，确保 Engine 停机时不中断正在处理的请求：`ConcurrentDictionary` 跟踪所有进行中请求，`RequestScope`（IDisposable）在入口自动注册、Dispose 时完成；停机时新请求抛 `AgentException(DependencyUnavailable)`，轮询等待进行中请求完成，超过 `Shutdown:TimeoutSeconds`（默认 30 秒）后记录 Warning（不强制终止请求）。停机顺序：ApplicationStopping → ShutdownService.ShutdownAsync(timeout) → RedisRegistry.DeregisterAsync()，编排依赖 `Program.cs`。核心功能已实现，尚缺单元测试覆盖。

### 服务注册

每个 Engine 实例启动后生成唯一 EngineId，将自身信息写入 Redis 并周期性发送心跳，停机时主动注销：

```text
HeartbeatService (BackgroundService)
  → RedisRegistry.RegisterAsync / HeartbeatAsync / DeregisterAsync
  → Redis String (engine:registry:{engineId}, TTL=30s)
  → Redis Set (engine:registry:index)

停机: ApplicationStopping → ShutdownService.ShutdownAsync → DeregisterAsync
```

- 心跳周期性更新注册信息并刷新 TTL，综合内存、GC、线程池压力计算负载值，随心跳发布支持的 intent。
- Redis 不可用时降级为孤岛模式运行，不阻塞 Engine 启动。
- 注销失败依赖 TTL 自然过期；进程异常退出会暂时留下索引成员，Router 发现注册值已过期后会清理该成员。
- 心跳单线程顺序执行，`IsRegistered` 非 volatile；注册、心跳、索引自修复与注销均有单元测试覆盖（`OpenAgent.Engine.Tests/Registry/`）。Router 侧消费方式见 [Router 文档](router.md)。

## 代码入口

- Host 端点：`Backend/src/OpenAgent.Engine.Host/Endpoints/`（按域 Chat/Agents/Provider/Conversations/Files 与 Admin/{Agents,Models,Mcp,Skills,Runtime} 的 Map* 入口；RAG 端点暂留 `Extensions/ConfigurationEndpointExtensions`）、`Extensions/`（EndpointExtensions、ConfigurationEndpointExtensions 汇总门面）、`Streaming/`（AgentStreamWriter、StreamingHeartbeat、StreamingResponseHeaders、StreamingPayloadFactory）、`Middleware/`、`Program.cs`、`EngineErrorHandling.cs`。
- 共享错误设施：`Backend/src/OpenAgent.Engine/Hosting/Errors/`（AgentProblem.cs、AgentExceptionHandling.cs）。
- 运行时管理：`Backend/src/OpenAgent.Engine/Redis/`（RedisRegistry、RedisHealthCheck、ConfigHealthCheck、LlmHealthCheck）、`Runtime/`（ShutdownService、RequestScope、HeartbeatService）。
- 测试：`Backend/tests/OpenAgent.Engine.Tests/`（Hosting/、Registry/、HealthChecks/）。
