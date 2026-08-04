# ErrorHandling - 设计文档

## 架构概览

ErrorHandling 模块通过两个中间件实现分层异常处理。`SseErrorHandlerMiddleware` 位于 `GlobalExceptionHandlerMiddleware` 之前，确保 SSE 端点的异常以 SSE 事件格式返回，而常规端点的异常由全局中间件统一转换为 ProblemDetails。

```
HTTP 请求
    │
    ▼
┌──────────────────────────────────┐
│  SseErrorHandlerMiddleware        │
│  路径包含 "/sse"？                │
│  ├─ 是 → 捕获异常，输出 SSE 事件  │
│  └─ 否 → 传递至下一中间件         │
└──────────────┬───────────────────┘
               │
               ▼
┌──────────────────────────────────┐
│  GlobalExceptionHandlerMiddleware │
│  捕获所有未处理异常               │
│  ├─ 响应已开始 → 重新抛出         │
│  └─ 响应未开始 → ProblemDetails   │
└──────────────┬───────────────────┘
               │
               ▼
┌──────────────────────────────────┐
│  Endpoint Handlers               │
│  (ChatApi 端点等)                │
└──────────────────────────────────┘
```

## 关键文件

| 文件路径 | 职责 |
|----------|------|
| `src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs` | 全局异常捕获，映射为 ProblemDetails |
| `src/Host/Middleware/SseErrorHandlerMiddleware.cs` | SSE 端点异常处理，输出 SSE 格式错误事件 |
| `src/Host/StreamingPayloadFactory.cs` | 流式错误载荷构造（CreateErrorPayload） |
| `src/Host/Program.cs` | 中间件注册顺序 |

## 接口签名

### GlobalExceptionHandlerMiddleware

```csharp
// 文件: src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs
internal class GlobalExceptionHandlerMiddleware
{
    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger);
    public async Task InvokeAsync(HttpContext context);
}
```

核心私有方法：

```csharp
private async Task HandleExceptionAsync(HttpContext context, Exception exception);
private static (int statusCode, ProblemDetails problemDetails) MapExceptionToProblemDetails(Exception exception, string traceId, string instance);
private static int MapAgentErrorCode(AgentErrorCode errorCode);
private static ProblemDetails CreateProblemDetails(string type, string title, int status, string detail, string? instance, string traceId, params (string key, object value)[] extensions);
```

### SseErrorHandlerMiddleware

```csharp
// 文件: src/Host/Middleware/SseErrorHandlerMiddleware.cs
internal class SseErrorHandlerMiddleware
{
    public SseErrorHandlerMiddleware(RequestDelegate next, ILogger<SseErrorHandlerMiddleware> logger);
    public async Task InvokeAsync(HttpContext context);
}
```

核心私有方法：

```csharp
private static bool IsSseEndpoint(HttpContext context);
private async Task HandleSseErrorAsync(HttpContext context, Exception exception);
```

### StreamingPayloadFactory

```csharp
// 文件: src/Host/StreamingPayloadFactory.cs
internal static class StreamingPayloadFactory
{
    public static StreamingErrorPayload CreateErrorPayload(Exception exception, string traceId);
    public static NdjsonStreamEvent CreateContentEvent(string content, string traceId);
    public static NdjsonStreamEvent CreateErrorEvent(StreamingErrorPayload error, string traceId);
    public static NdjsonStreamEvent CreateDoneEvent(string traceId, string status = "completed");
}
```

## 数据依赖

### 输入

- `Exception` 及其子类：`UnauthorizedAccessException`、`HumanApprovalRequiredException`、`AgentException`、`TimeoutException`
- `AgentErrorCode` 枚举（`Agent.Contracts/Requests/AgentErrorCode.cs`）
- `HttpContext`：用于获取 TraceId、请求路径、响应状态

### 输出

**ProblemDetails**（`Microsoft.AspNetCore.Mvc.ProblemDetails`）：

```json
{
  "type": "https://error.agent.com/{type}",
  "title": "ErrorTitle",
  "status": 500,
  "detail": "Error detail message",
  "instance": "Exception additional info (varies by exception type)",
  "traceId": "00-abc123-xyz789-01",
  "timestamp": "2026-06-10T06:30:00.0000000+00:00",
  "errorCode": 9001
}
```

**StreamingErrorPayload**（`src/Host/StreamingPayloadFactory.cs`）：

```json
{
  "type": "https://error.agent.com/{errorcode}",
  "title": "ErrorCodeName",
  "detail": "Error message",
  "traceId": "00-abc123-xyz789-01"
}
```

## 异常映射详情

### GlobalExceptionHandlerMiddleware 映射表

| 异常类型 | 状态码 | Type URI | Title | Detail 来源 |
|----------|--------|----------|-------|------------|
| UnauthorizedAccessException | 403 | https://error.agent.com/unauthorized | Unauthorized | "Access denied due to insufficient permissions" |
| HumanApprovalRequiredException | 202 | https://error.agent.com/approval-required | HumanApprovalRequired | "Action requires human approval" |
| AgentException | 按 ErrorCode | https://error.agent.com/{errorcode} | ErrorCode.ToString() | agentEx.Message |
| TimeoutException | 504 | https://error.agent.com/timeout | GatewayTimeout | "The request timed out" |
| 其他 | 500 | https://error.agent.com/internal-error | InternalServerError | "An unexpected error occurred" |

### AgentErrorCode → HTTP 状态码映射

| AgentErrorCode | HTTP 状态码 | 语义 |
|----------------|-------------|------|
| UnauthorizedSkill | 403 | 技能未授权 |
| AudiencePermissionDenied | 403 | 受众权限拒绝 |
| SkillNotFound | 404 | 技能未找到 |
| McpToolNotFound | 404 | MCP 工具未找到 |
| RagIndexNotFound | 404 | RAG 索引未找到 |
| LlmModelNotFound | 404 | LLM 模型未找到 |
| SkillQuotaExceeded | 429 | 技能配额超限 |
| LlmQuotaExceeded | 429 | LLM 配额超限 |
| InvalidRequest | 400 | 无效请求 |
| MissingRequiredField | 400 | 缺少必填字段 |
| InvalidIdempotencyKey | 400 | 无效幂等键 |
| SkillValidationFailed | 400 | 技能验证失败 |
| DependencyUnavailable | 503 | 依赖不可用 |
| 其他 | 500 | 内部错误 |

### StreamingPayloadFactory 映射表

| 异常类型 | Type URI | Title | Detail |
|----------|----------|-------|--------|
| AgentException | https://error.agent.com/{errorcode} | ErrorCode.ToString() | ae.Message |
| TimeoutException | https://error.agent.com/timeout | GatewayTimeout | "An unexpected error occurred during streaming" |
| 其他 | https://error.agent.com/internal-error | InternalServerError | "An unexpected error occurred during streaming" |

## 中间件注册顺序

在 `Program.cs` 中：

```csharp
app.UseAgentHost(builder.Configuration);
app.UseMiddleware<SseErrorHandlerMiddleware>();    // 1. SSE 错误处理（先注册）
app.UseMiddleware<GlobalExceptionHandlerMiddleware>(); // 2. 全局异常处理（后注册）
app.MapControllers();
app.MapAgentEndpoints();
```

> SSE 错误处理器先于全局异常处理器注册，确保 SSE 端点的异常被 SseErrorHandlerMiddleware 捕获，而非被 GlobalExceptionHandlerMiddleware 以 ProblemDetails 格式返回。

## SSE 错误输出格式

SSE 端点异常时，`SseErrorHandlerMiddleware` 输出：

```
event: error
data: {"Type":"https://error.agent.com/...","Title":"...","Detail":"...","TraceId":"..."}

event: done
data: [DONE]

```

## 序列化配置

### GlobalExceptionHandlerMiddleware

```csharp
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
```

### SseErrorHandlerMiddleware

使用 `JsonSerializer.Serialize` 默认配置（PascalCase）。
