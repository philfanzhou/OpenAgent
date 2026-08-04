# Agent.Hosting 需求规格说明 (Requirement Specification)

## 1. 模块定位
`Agent.Hosting` 是将纯粹底层业务逻辑（`Agent.Core` 等）转化为**可对外提供网络服务的宿主层（ASP.NET Core Wrapper）**。
它**不包含任何核心的 AI 调度和工具执行逻辑**，也**不包含 DevOps 维度的部署脚本**（后者属于 `Agent.Deploy`）。它专注于处理 HTTP 协议、网络流、中间件管线、全局异常拦截、服务注册（DI）以及 API 文档（Swagger）。

简而言之：`Agent.Core` 决定了“Agent 能做什么”，而 `Agent.Hosting` 决定了“外部系统如何通过网络调用 Agent”。

## 2. 核心需求拆解

### 2.1 HTTP 协议与通信模式支持
*   **多模式端点暴露**: 必须提供标准化且版本控制的 API（如 Minimal APIs 或 Controllers），支持以下三种对话模式：
    *   **Blocking (Request-Response)**: 传统的短连接，等待整个 LLM 推理完成后一次性返回完整 JSON。
    *   **NDJSON Streaming**: 基于 `IAsyncEnumerable` 返回换行符分隔的 JSON 数据流，适用于现代前端的简单流式解析。
    *   **Server-Sent Events (SSE)**: 提供标准的 `text/event-stream` 端点，支持复杂的长连接流式响应（逐字输出），优化首字节响应时间（TTFB）。
*   **连接断开感知 (Client Disconnects)**: 必须捕获来自 HTTP 请求的 `HttpContext.RequestAborted` 信号，并将其作为 `CancellationToken` 传递给底层的 `Agent.Core`。一旦客户端异常断开（如关闭浏览器网页），立即中止底层昂贵的 LLM 推理和外部 MCP 调用。

### 2.2 ASP.NET Core 中间件管线 (Middleware Pipeline)
*   **全局异常处理 (Global Exception Handling)**: 必须实现自定义的异常拦截中间件（如 `IExceptionHandler`）。拦截底层抛出的各种异常（如权限不足的 `UnauthorizedAccessException`，或外部调用的 `TimeoutException`，以及契约层定义的 `ToolExecutionException`, `AudiencePermissionDeniedException` 等），并将其转化为标准化的 **RFC 7807 Problem Details** 格式返回给前端，包含具体的 `AgentErrorCode`。**严禁直接向外暴露 500 堆栈错误**。
*   **SSE 错误熔断**: 在 SSE（流式输出）建立连接后如果发生异常，必须能够安全地发送包含错误信息的终端事件（Event: error），并优雅地断开流，防止前端无限等待。
*   **跨域资源共享 (CORS)**: 提供灵活且安全的 CORS 策略配置，允许前端工程（如本地测试的 Web 页面）安全地调用 Agent 接口。
*   **内容协商与序列化**: 统一配置 `System.Text.Json` 的序列化行为（如驼峰命名、忽略 null 值），确保跨组件的数据契约一致性。

### 2.3 依赖注入与启动引导 (DI & Bootstrapping)
*   **扩展方法聚合**: 必须提供类似于 `AddAgentMatrix()` 和 `UseAgentMatrix()` 的一键式扩展方法，将 `Agent.Core` 内部复杂的组件（如 SK Kernel、MCP Client、Redis 缓存、RAG 客户端等）的注册逻辑封装和屏蔽起来，使得最终的宿主工程（如 WebAPI）可以保持 `Program.cs` 的极度简洁。
*   **健康检查 (Health Checks)**: 提供 `/health` 端点，供 K8s 或外部监控探针探测服务存活状态。
*   **配置绑定 (Options Pattern)**: 负责从 `appsettings.json` 或环境变量中读取配置树（如 `AgentOptions`），并注册为强类型的 `IOptions<T>` 供底层使用。

### 2.4 可观测性集成层 (Observability Hooks)
*   **OpenTelemetry 探针**: 在 Hosting 层集成 `AddOpenTelemetry()`，自动捕获进入的 HTTP 请求（Incoming Requests），提取外部透传的 `TraceId`，并记录 Controller/Minimal API 级别的耗时（Metrics）与日志（Logs），无缝传递给下游模块。
*   **API 文档生成 (Swagger/OpenAPI)**: 自动扫描并生成规范的 OpenAPI (Swagger) 文档。
    *   **多版本支持**: 支持 API 版本控制（如 `/v1/agent/chat`），在 Swagger UI 中提供版本切换。
    *   **安全定义**: Swagger UI 必须配置 OAuth2 / Bearer Token 鉴权输入框，便于开发与测试人员直接在页面发起受保护的调用。

### 2.5 调试与测试支持 (TestHost & TestPage Fallback)
*   **独立于生产环境的 Debug 模式**: 必须考虑到开发人员本地调试的体验。系统应支持通过简单的配置项或环境变量，**最小化拉起 Agent 流程**，绕过复杂的 SSO 鉴权、Redis 依赖和复杂的 Router 路由。
*   **开发用测试宿主 (Agent.TestHost)**: 确保与 `Agent.TestHost` 的兼容性。`Agent.Hosting` 提供的扩展方法应该足够灵活，允许 `Agent.TestHost` 在不需要完整矩阵架构的情况下，单机启动 `Agent.Core` 进行特定 Skill 或 MCP 的单元/集成测试。
*   **沙盒降级机制 (Sandbox Fallback)**: 当外部依赖（如真实的 RAG 系统或企业级 SSO）不可用时，Hosting 层应支持注入 Mock 服务（如 `MockPermissionEvaluator`），以保证本地开发不被阻塞。
