# ErrorHandling - 详细规格

## 功能需求 (FR)

### FR-01: 全局异常捕获与映射

- **FR-01.1**: `GlobalExceptionHandlerMiddleware` 捕获所有未处理异常
- **FR-01.2**: 若响应已开始（`context.Response.HasStarted`），重新抛出异常而非写入 ProblemDetails
- **FR-01.3**: 响应 Content-Type 为 `application/problem+json`
- **FR-01.4**: 所有 ProblemDetails 响应包含 `traceId` 和 `timestamp` 扩展字段

### FR-02: UnauthorizedAccessException 映射

- **FR-02.1**: 映射为 HTTP 403
- **FR-02.2**: Type 为 `https://error.agent.com/unauthorized`
- **FR-02.3**: Title 为 `"Unauthorized"`
- **FR-02.4**: Detail 为 `"Access denied due to insufficient permissions"`

### FR-03: HumanApprovalRequiredException 映射

- **FR-03.1**: 映射为 HTTP 202
- **FR-03.2**: Type 为 `https://error.agent.com/approval-required`
- **FR-03.3**: Title 为 `"HumanApprovalRequired"`
- **FR-03.4**: Detail 为 `"Action requires human approval"`
- **FR-03.5**: 扩展字段包含 `approvalToken`（来自异常的 `ApprovalToken` 属性，默认空字符串）
- **FR-03.6**: 扩展字段包含 `actionDescription`（来自异常的 `ActionDescription` 属性）

### FR-04: AgentException 映射

- **FR-04.1**: 根据 `AgentErrorCode` 映射 HTTP 状态码
- **FR-04.2**: Type 为 `https://error.agent.com/{errorcode}`（errorcode 为小写）
- **FR-04.3**: Title 为 `AgentErrorCode.ToString()`
- **FR-04.4**: Detail 为 `agentEx.Message`
- **FR-04.5**: 扩展字段包含 `errorCode`（int 值）

### FR-05: AgentErrorCode 到 HTTP 状态码映射

- **FR-05.1**: `UnauthorizedSkill`、`AudiencePermissionDenied` → 403
- **FR-05.2**: `SkillNotFound`、`McpToolNotFound`、`RagIndexNotFound`、`LlmModelNotFound` → 404
- **FR-05.3**: `SkillQuotaExceeded`、`LlmQuotaExceeded` → 429
- **FR-05.4**: `InvalidRequest`、`MissingRequiredField`、`InvalidIdempotencyKey`、`SkillValidationFailed` → 400
- **FR-05.5**: `DependencyUnavailable` → 503
- **FR-05.6**: 其他 ErrorCode → 500

### FR-06: TimeoutException 映射

- **FR-06.1**: 映射为 HTTP 504
- **FR-06.2**: Type 为 `https://error.agent.com/timeout`
- **FR-06.3**: Title 为 `"GatewayTimeout"`
- **FR-06.4**: Detail 为 `"The request timed out"`

### FR-07: 未知异常映射

- **FR-07.1**: 映射为 HTTP 500
- **FR-07.2**: Type 为 `https://error.agent.com/internal-error`
- **FR-07.3**: Title 为 `"InternalServerError"`
- **FR-07.4**: Detail 为 `"An unexpected error occurred"`（不暴露异常详情）

### FR-08: SSE 端点错误处理

- **FR-08.1**: `SseErrorHandlerMiddleware` 仅对 SSE 端点生效（路径包含 `/sse`）
- **FR-08.2**: 非 SSE 端点直接传递至下一中间件
- **FR-08.3**: 异常时写入 SSE error 事件：`event: error\ndata: {json}\n\n`
- **FR-08.4**: 随后写入 SSE done 事件：`event: done\ndata: [DONE]\n\n`
- **FR-08.5**: 若 `context.RequestAborted.IsCancellationRequested`，跳过错误写入
- **FR-08.6**: 若响应未开始，设置 Content-Type 为 `text/event-stream`、Cache-Control 为 `no-cache`、Connection 为 `keep-alive`、StatusCode 为 200

### FR-09: 流式错误载荷构造

- **FR-09.1**: `StreamingPayloadFactory.CreateErrorPayload` 根据异常类型构造 `StreamingErrorPayload`
- **FR-09.2**: `AgentException` → Type 为 `https://error.agent.com/{errorcode}`，Title 为 ErrorCode 名称，Detail 为异常消息
- **FR-09.3**: `TimeoutException` → Type 为 `https://error.agent.com/timeout`，Title 为 `"GatewayTimeout"`
- **FR-09.4**: 其他异常 → Type 为 `https://error.agent.com/internal-error`，Title 为 `"InternalServerError"`，Detail 为 `"An unexpected error occurred during streaming"`

### FR-10: 日志记录

- **FR-10.1**: `GlobalExceptionHandlerMiddleware` 捕获异常时记录 `LogError`，包含 TraceId
- **FR-10.2**: 响应已开始时记录 `LogWarning`
- **FR-10.3**: `SseErrorHandlerMiddleware` 捕获异常时记录 `LogError`

## 验收标准 (AC)

- **[当前无测试覆盖]** GlobalExceptionHandlerMiddleware 的异常映射
- **[当前无测试覆盖]** SseErrorHandlerMiddleware 的 SSE 错误输出
- **[当前无测试覆盖]** StreamingPayloadFactory 的错误载荷构造
- **[当前无测试覆盖]** AgentErrorCode 到 HTTP 状态码的映射
- **[当前无测试覆盖]** 响应已开始时的重新抛出行为
- **[当前无测试覆盖]** SSE 中间件对非 SSE 端点的跳过行为

## ProblemDetails 数据模型

| 字段 | 类型 | 说明 |
|------|------|------|
| Type | string | 错误类型 URI |
| Title | string | 错误标题 |
| Status | int | HTTP 状态码 |
| Detail | string | 错误详情 |
| Instance | string | 异常附加信息（因异常类型而异：UnauthorizedAccessException/TimeoutException 为 exception.Message，HumanApprovalRequiredException 为 approvalEx.Message，AgentException 为 agentEx.Details ?? agentEx.Message，未知异常为 "Please contact support if the problem persists"） |
| Extensions["traceId"] | string | 追踪标识 |
| Extensions["timestamp"] | DateTimeOffset | 时间戳（UTC） |
| Extensions["errorCode"] | int | AgentErrorCode 值（仅 AgentException） |
| Extensions["approvalToken"] | string | 审批令牌（仅 HumanApprovalRequiredException） |
| Extensions["actionDescription"] | string | 操作描述（仅 HumanApprovalRequiredException） |

## StreamingErrorPayload 数据模型

| 字段 | 类型 | 说明 |
|------|------|------|
| Type | string | 错误类型 URI |
| Title | string | 错误标题 |
| Detail | string | 错误详情 |
| TraceId | string | 追踪标识 |
