# Execution — 执行管线与核心调度

本域包含 Agent 平台的核心执行逻辑：执行入口编排、流式推理与统一错误处理。会话锁与持久化见 [会话文档](conversation.md)；代码目录与扩展方式见 [开发指南](../overview/DevelopmentGuide.md)。

## 执行编排

RuntimeAgentExecutor 提供同步 ExecuteAsync 和流式 ExecuteStreamingAsync，接收 AgentRequest、IAgentUserContext 与 CancellationToken。

请求流程：

1. Engine.Host 通过 Engine/Hosting 认证/错误管道、AgentUserContextMiddleware 与 EngineAdmissionMiddleware 建立身份并执行入口治理。
2. ConversationAgentResolver 仅从会话路由元数据选择 Agent（不读取完整消息），IAgentRuntimeResolver 获取授权后的 Agent 配置和模型，创建 TurnContext。
3. 授权后进入 Host 单例 TurnAdmission 的有界准入；等待期间不持有会话锁、不加载消息历史、不解析附件或准备工具。获准后，带附件时先确保会话，再通过 FileAssetRequestResolver 校验和解析文件。
4. RuntimePreparation 准备平台历史、能力、MCP 与 Skill；RuntimeAgentExecutor 调用 IAgentRuntimeDriver。生产驱动通过 SDK 启动 OpenCode ACP，不调用 AIAgent.RunAsync。
5. AcpEventMapper 转换正文、reasoning、工具调用/结果与计划事件；平台保存会话成功后发出终态。ACP 未提供本轮计费 token 时保留 null，不作估算。作用域释放持有的资源。

完整运行边界、准入配置与容量实测见 [OpenCode ACP](../integrations/opencode-runtime.md#n03并发准入排队与取消)。

异常、取消与协议：

- RuntimeAgentExecutor 不把异常转成 Success=false 的 AgentResponse，异常向上传播；工具调用的局部错误由工具机制隔离（见下文错误处理）。
- 共享 Engine/Hosting/Errors/AgentExceptionHandling.cs 负责 HTTP ProblemDetails 映射；Engine.Host/EngineErrorHandling.cs 添加 Provider 错误与 SSE 文案，响应已开始时写 error/done，客户端断开按取消处理。
- Application 返回 IAsyncEnumerable&lt;AgentStreamEvent&gt;；Host 的 AgentStreamWriter 预读首个事件后才建立 SSE，使用实际 CancellationToken。授权失败或队列满可以返回普通 HTTP 错误；进入 SSE 后的失败继续使用既有 error/done。
- 附加 `execution_status` 事件携带 `queued` 或 `started` 与可选 `queueMilliseconds`；旧客户端忽略未知事件，新客户端只在 UI 展示等待，状态文本不写入 assistant 正文。`started` 表示获准准备，ACP 身份仍由原 `runtime` 事件报告。
- `RuntimeQueueFull` 返回 HTTP 429，`RuntimeQueueTimeout` 在流前返回 HTTP 503，均带 Retry-After 与 retryAfterSeconds；流内超时在 error 事件带同一错误码与重试间隔。`RuntimeExecutionTimeout` 返回 HTTP 504（若已开流则 error/done）。
- 取消/失败时 PlatformChatHistory 在释放期间使用 CancellationToken.None 写回 partial；初始化中途失败清理已创建的功能资源，并保留原始异常。
- HTTP 中间件顺序由 Host Program 配置，执行域不定义或注册 Web 中间件。请求日志、Trace 与排障入口见 [观测文档](../overview/Observability.md)。

## 流式输出

流式推理通过 `IAsyncEnumerable<AgentStreamEvent>` 持续产出模型内容，支持工具调用中间状态透传、取消信号传播和异常向上报告，流式路径经过完整中间件链。

```text
RuntimeAgentExecutor.ExecuteStreamingAsync
  → IAgentRuntimeDriver（OpenCode ACP session/update）
  → AcpEventMapper 合并工具增量、去除桥前缀
  → AgentStreamEvent
  → assistant chunks / tool markers / usage
```

- 工具调用产出 `ToolCall` 类型事件（含 ToolName/ToolCallId/ToolArguments），按 CallId/Name 去重后广播；ReasoningContent 作为 `Reasoning` 类型事件产出。
- Provider usage 只随 `done` SSE 事件发送，缺失时为 `null`；不根据流式文本片段估算 Token。
- SSE/NDJSON 协议格式化属宿主层，Application 不负责。

## 错误处理

统一错误处理定义执行层异常分类、传播规则和会话写回保障；完整记录与重放的剩余范围见 [后续任务](../planning/TODO.md)。

| 能力 | 行为 |
|---|---|
| 异常分类 | `AgentException` 携带 `AgentErrorCode`（10 大类 30+ 错误码）|
| 异常传播 | 执行层不捕获异常，向上传播至 Host 注册的共享中间件映射 |
| 工具异常隔离 | 工具返回结构化 `ToolResult`；未捕获异常脱敏后以统一错误信封回传模型，不中断推理循环 |
| 统一错误信封 | `{"error":"...","code":"...","hint":"...",("timedOut":true)添加于超时,("errorId":"...")添加于脱敏异常}`；code 稳定供程序判别，hint 给模型可行动修正路径 |
| 工具调用超时 | 单次调用超过 `AgentExecution:ToolCallTimeoutSeconds`（默认 300，≤0 不限时）被主动取消，以带 `timedOut` 标记的错误结果回传模型，会话不中断 |
| 结果预算截断 | 工具结果超过分级字符预算（读 80k/执行 40k/MCP 48k/默认 60k，`AgentExecution:*ToolResultCharBudget`，≤0 不限）做头尾保留截断并附收窄提示 |
| 会话写回保障 | 已准备的轮次在取消/失败时写回 partial 消息（`CancellationToken.None`）；用户取消为 Cancelled，执行超时/错误为 Failed。尚未获准的请求不创建消息或运行终态 |

分层行为：

```text
执行层（RuntimeAgentExecutor）: 不捕获异常；AgentException 与通用 Exception 均向上传播

能力层（CapabilityDefinition.Invoke → Task<ToolResult>）:
  校验/业务错误 → ToolResult.Error(message, code, hint)，Content 即错误信封

工具执行（IsolatedToolFunction 包装所有平台能力工具，模型侧唯一出口）:
  ToolResult(IsError) → 原样回传信封（不截断）
  ToolResult/字符串   → 过分级预算，超限做头尾保留截断 + 收窄提示
  Exception          → 脱敏信封 {"error":"Tool '...' failed with an unexpected error.","code":"tool_error","errorId":"..."}；
                       原始异常只进日志（errorId 关联），不透传给模型
  调用超时/传输层取消（外层未取消）→ {"error":"Tool '...' timed out ...","code":"tool_timeout","timedOut":true}
  运行级取消（用户中止/停机）→ 照常 rethrow OperationCanceledException

异常映射（Engine/Hosting 中间件，Host 注册）:
  AgentExceptionHandlerMiddleware 捕获异常 → 映射为 HTTP 错误响应（ProblemDetails）
```

- 流式路径由共享中间件在响应已开始时写 error/done，未开始时返回 ProblemDetails；HTTP 状态码映射属宿主层。
- 预算按字符计（≈4 字符/token），非精确 token 计数。

## 验证入口

| 场景 | 对应测试目录/文件（Backend/tests/ 下） |
|---|---|
| 工具循环、Skill 工具列表、模型与 usage | OpenAgent.Application.Tests/Execution/AgentExecutor*Tests.cs |
| 功能贡献、成功释放、失败清理 | OpenAgent.Application.Tests/Execution/AgentFeatureCompositionTests.cs |
| 历史、取消 partial 与锁 | OpenAgent.Application.Tests/Conversation/History/、Lock/ |
| 错误映射与 SSE 终态 | OpenAgent.Engine.Tests/Hosting/（AgentExceptionHandlerMiddlewareTests、AgentStreamWriterTests）|
| 工具异常隔离、超时与预算 | OpenAgent.Application.Tests/Tooling/Invocation/（ToolFailureIsolationTests、ToolResultBudgetTests）|

验证身份与授权、同步/流式输出、取消、工具配对、失败清理和 HTTP/SSE 边界；不对已移交 Host 的异常转换重复创建 Application 测试。

## 代码入口

- Application：Backend/src/OpenAgent.Application/Execution/RuntimeAgentExecutor.cs、RuntimePreparation.cs、RuntimePreparationScope.cs。
- Host：Backend/src/OpenAgent.Engine.Host/Program.cs、EngineErrorHandling.cs、Extensions/AgentChatEndpointExtensions.cs、AgentStreamWriter.cs。
- HTTP 错误：Backend/src/OpenAgent.Engine/Hosting/Errors/AgentExceptionHandling.cs。
- 工具隔离与预算：Backend/src/OpenAgent.Application/Tooling/Invocation/（IsolatedToolFunction、ToolResultBudgets、AgentExecutionOptions）。
- 契约：Backend/src/OpenAgent.Contracts/Security/Exceptions.cs、Requests/AgentErrorCode.cs、Capabilities/ToolResult.cs。
