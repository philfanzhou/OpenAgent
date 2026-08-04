# ErrorHandling - 测试文档

## 现有测试

**当前无测试覆盖** — ErrorHandling 模块没有专门的测试文件。

---

## 缺失测试场景

### GlobalExceptionHandlerMiddleware

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-01 | Given 请求抛出 UnauthorizedAccessException，When 中间件处理，Then 返回 403 和 ProblemDetails（Type=https://error.agent.com/unauthorized） | 高 |
| MT-02 | Given 请求抛出 HumanApprovalRequiredException，When 中间件处理，Then 返回 202 和 ProblemDetails（含 approvalToken 和 actionDescription 扩展） | 高 |
| MT-03 | Given 请求抛出 AgentException(ErrorCode=SkillNotFound)，When 中间件处理，Then 返回 404 和 ProblemDetails（Type=https://error.agent.com/skillnotfound） | 高 |
| MT-04 | Given 请求抛出 AgentException(ErrorCode=SkillQuotaExceeded)，When 中间件处理，Then 返回 429 | 高 |
| MT-05 | Given 请求抛出 AgentException(ErrorCode=InvalidRequest)，When 中间件处理，Then 返回 400 | 高 |
| MT-06 | Given 请求抛出 AgentException(ErrorCode=DependencyUnavailable)，When 中间件处理，Then 返回 503 | 高 |
| MT-07 | Given 请求抛出 AgentException(ErrorCode=InternalError)，When 中间件处理，Then 返回 500 | 高 |
| MT-08 | Given 请求抛出 TimeoutException，When 中间件处理，Then 返回 504 和 ProblemDetails（Type=https://error.agent.com/timeout） | 高 |
| MT-09 | Given 请求抛出未知异常，When 中间件处理，Then 返回 500 和 ProblemDetails（Detail="Please contact support if the problem persists"） | 高 |
| MT-10 | Given 响应已开始，When 中间件捕获异常，Then 重新抛出异常而非写入 ProblemDetails | 高 |
| MT-11 | Given 任意异常，When 中间件处理，Then ProblemDetails 包含 traceId 和 timestamp 扩展字段 | 高 |
| MT-12 | Given 任意异常，When 中间件处理，Then 响应 Content-Type 为 application/problem+json | 高 |
| MT-13 | Given AgentException，When 中间件处理，Then ProblemDetails 包含 errorCode 扩展字段 | 中 |

### AgentErrorCode 映射

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-14 | Given UnauthorizedSkill，When 映射，Then 返回 403 | 高 |
| MT-15 | Given AudiencePermissionDenied，When 映射，Then 返回 403 | 高 |
| MT-16 | Given McpToolNotFound，When 映射，Then 返回 404 | 高 |
| MT-17 | Given RagIndexNotFound，When 映射，Then 返回 404 | 高 |
| MT-18 | Given LlmModelNotFound，When 映射，Then 返回 404 | 高 |
| MT-19 | Given LlmQuotaExceeded，When 映射，Then 返回 429 | 高 |
| MT-20 | Given MissingRequiredField，When 映射，Then 返回 400 | 高 |
| MT-21 | Given InvalidIdempotencyKey，When 映射，Then 返回 400 | 高 |
| MT-22 | Given SkillValidationFailed，When 映射，Then 返回 400 | 高 |
| MT-23 | Given Success(0)，When 映射，Then 返回 500（默认分支） | 中 |
| MT-24 | Given PipelineExecutionFailed，When 映射，Then 返回 500（默认分支） | 中 |

### SseErrorHandlerMiddleware

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-25 | Given 请求路径包含 /sse 且抛出异常，When 中间件处理，Then 输出 SSE error 事件 + done 事件 | 高 |
| MT-26 | Given 请求路径不包含 /sse，When 中间件处理，Then 直接传递至下一中间件 | 高 |
| MT-27 | Given SSE 端点抛出异常且 RequestAborted，When 中间件处理，Then 跳过错误写入 | 高 |
| MT-28 | Given SSE 端点抛出异常且响应未开始，When 中间件处理，Then 设置 Content-Type=text/event-stream, StatusCode=200 | 高 |
| MT-29 | Given SSE 端点抛出异常且响应已开始，When 中间件处理，Then 不修改响应头 | 中 |

### StreamingPayloadFactory.CreateErrorPayload

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-30 | Given AgentException，When CreateErrorPayload，Then Type 为 https://error.agent.com/{errorcode}，Title 为 ErrorCode 名称，Detail 为异常消息 | 高 |
| MT-31 | Given TimeoutException，When CreateErrorPayload，Then Type 为 https://error.agent.com/timeout，Title 为 GatewayTimeout | 高 |
| MT-32 | Given 未知异常，When CreateErrorPayload，Then Type 为 https://error.agent.com/internal-error，Title 为 InternalServerError，Detail 为 "An unexpected error occurred during streaming" | 高 |

### 中间件集成

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-33 | Given SSE 端点异常，When 经过中间件管道，Then SseErrorHandlerMiddleware 捕获异常而非 GlobalExceptionHandlerMiddleware | 高 |
| MT-34 | Given 非 SSE 端点异常，When 经过中间件管道，Then GlobalExceptionHandlerMiddleware 捕获异常 | 高 |
