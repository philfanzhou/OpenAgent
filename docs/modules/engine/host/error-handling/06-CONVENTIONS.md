# ErrorHandling - 约定规范

## 命名约定

### 中间件命名

- 中间件类名以 `Middleware` 后缀结尾：`GlobalExceptionHandlerMiddleware`、`SseErrorHandlerMiddleware`
- 中间件类为 `internal` 访问级别
- 构造函数接收 `RequestDelegate next` 和 `ILogger<T>` 参数
- 核心方法命名为 `InvokeAsync`

### 方法命名

- 异常处理方法以 `Handle` 为前缀：`HandleExceptionAsync`、`HandleSseErrorAsync`
- 映射方法以 `Map` 为前缀：`MapExceptionToProblemDetails`、`MapAgentErrorCode`
- 检测方法以 `Is` 为前缀：`IsSseEndpoint`
- 构造方法以 `Create` 为前缀：`CreateProblemDetails`、`CreateErrorPayload`

### 载荷模型命名

- 错误载荷：`StreamingErrorPayload`（`sealed` 类，`init` 属性）
- NDJSON 事件：`NdjsonStreamEvent`（`sealed` 类，`init` 属性）
- 必填属性使用 `required` 修饰符

## 错误类型 URI 约定

所有错误类型 URI 遵循统一格式：`https://error.agent.com/{type}`

| 异常/错误码 | Type URI |
|-------------|----------|
| UnauthorizedAccessException | https://error.agent.com/unauthorized |
| HumanApprovalRequiredException | https://error.agent.com/approval-required |
| AgentException | https://error.agent.com/{errorcode}（小写） |
| TimeoutException | https://error.agent.com/timeout |
| 未知异常 | https://error.agent.com/internal-error |

> AgentException 的 errorcode 使用 `ToString().ToLowerInvariant()` 转换，例如 `SkillNotFound` → `skillnotfound`。

## ProblemDetails 约定

### 必填扩展字段

所有 ProblemDetails 响应必须包含：

- `traceId`：追踪标识（string），来自 `Activity.Current.Id ?? context.TraceIdentifier`
- `timestamp`：时间戳（DateTimeOffset），使用 `DateTimeOffset.UtcNow`

### 条件扩展字段

- `errorCode`（int）：仅 AgentException 时包含
- `approvalToken`（string）：仅 HumanApprovalRequiredException 时包含，默认空字符串
- `actionDescription`（string）：仅 HumanApprovalRequiredException 时包含

### 序列化约定

- PropertyNamingPolicy：`CamelCase`
- 忽略 null 值：`JsonIgnoreCondition.WhenWritingNull`
- Content-Type：`application/problem+json`

## SSE 错误输出约定

### 事件格式

```
event: error
data: {json_payload}

event: done
data: [DONE]

```

- error 事件的 data 为 `StreamingErrorPayload` 的 JSON 序列化
- done 事件的 data 固定为 `[DONE]`
- 每个事件以 `\n\n` 结尾

### 响应头

- StatusCode：200
- Content-Type：`text/event-stream`
- Cache-Control：`no-cache`
- Connection：`keep-alive`

> 仅在响应未开始时设置响应头。

## 异常处理策略约定

### 响应已开始

- `GlobalExceptionHandlerMiddleware`：记录 LogWarning，使用 `ExceptionDispatchInfo.Capture(exception).Throw()` 重新抛出
- `SseErrorHandlerMiddleware`：不检查此条件，直接写入 SSE 事件

### 客户端中断

- `GlobalExceptionHandlerMiddleware`：不特殊处理客户端中断
- `SseErrorHandlerMiddleware`：若 `context.RequestAborted.IsCancellationRequested`，跳过错误写入

### 信息暴露控制

- `UnauthorizedAccessException`：Detail 为固定文本 "Access denied due to insufficient permissions"，`exception.Message` 暴露在 Instance 字段中
- `HumanApprovalRequiredException`：Detail 为固定文本 "Action requires human approval"
- `AgentException`：Detail 为 `agentEx.Message`，暴露异常消息
- `TimeoutException`：Detail 为固定文本 "The request timed out"
- 未知异常：Detail 为固定文本 "An unexpected error occurred"，不暴露内部异常详情

## 日志约定

### GlobalExceptionHandlerMiddleware

```csharp
_logger.LogError(exception, "Unhandled exception occurred. TraceId: {TraceId}", traceId);
_logger.LogWarning("Response has already started, skipping problem details response. TraceId: {TraceId}", traceId);
```

### SseErrorHandlerMiddleware

```csharp
_logger.LogError(exception, "SSE endpoint error occurred");
```

> [推断] SSE 中间件的日志未包含 TraceId，与全局中间件的日志格式不一致。

## 中间件注册顺序约定

SSE 错误处理器必须注册在全局异常处理器之前：

```csharp
app.UseMiddleware<SseErrorHandlerMiddleware>();      // 先注册
app.UseMiddleware<GlobalExceptionHandlerMiddleware>(); // 后注册
```

这确保 SSE 端点的异常被 SseErrorHandlerMiddleware 优先捕获，避免 SSE 端点返回 ProblemDetails 格式的错误响应。

## SSE 端点检测约定

通过请求路径包含 `/sse`（不区分大小写）判断：

```csharp
context.Request.Path.Value?.Contains("/sse", StringComparison.OrdinalIgnoreCase) == true
```

> [待确认] 此检测方式较为简单，可能误匹配路径中包含 "/sse" 的非 SSE 端点。
