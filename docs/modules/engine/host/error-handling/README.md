
## Feature


## 核心用户故事

作为客户端应用，我希望在请求失败时收到有意义的错误响应，以便能正确处理异常情况。

## 功能简介

ErrorHandling 模块提供了 Agent.Engine 的全局异常处理机制，确保所有未处理异常都能被转换为结构化的错误响应。模块包含两个中间件：`GlobalExceptionHandlerMiddleware` 处理常规 HTTP 请求的异常（返回 ProblemDetails），`SseErrorHandlerMiddleware` 专门处理 SSE 端点的异常（返回 SSE 格式错误事件）。同时，`StreamingPayloadFactory` 为流式端点提供统一的错误载荷构造。

## 组件一览

| 组件 | 职责 |
|------|------|
| GlobalExceptionHandlerMiddleware | 捕获所有未处理异常，映射为 ProblemDetails 响应 |
| SseErrorHandlerMiddleware | SSE 端点异常处理，以 SSE 事件格式输出错误 |
| StreamingPayloadFactory | 构造流式错误载荷（StreamingErrorPayload） |

## 异常映射概览

| 异常类型 | HTTP 状态码 | 错误类型 URI |
|----------|-------------|-------------|
| UnauthorizedAccessException | 403 | https://error.agent.com/unauthorized |
| HumanApprovalRequiredException | 202 | https://error.agent.com/approval-required |
| AgentException | 按 ErrorCode 映射 | https://error.agent.com/{errorcode} |
| TimeoutException | 504 | https://error.agent.com/timeout |
| 其他 | 500 | https://error.agent.com/internal-error |

## Specification


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

## Design


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

## Tasks


```json
[
  {
    "id": "T-01",
    "title": "实现 GlobalExceptionHandlerMiddleware",
    "description": "捕获所有未处理异常，根据异常类型映射为 ProblemDetails 响应",
    "status": "implemented",
    "source": "src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs:12-163"
  },
  {
    "id": "T-02",
    "title": "实现异常到 ProblemDetails 的映射",
    "description": "MapExceptionToProblemDetails：UnauthorizedAccessException→403, HumanApprovalRequiredException→202, AgentException→按ErrorCode映射, TimeoutException→504, 其他→500",
    "status": "implemented",
    "source": "src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs:61-108"
  },
  {
    "id": "T-03",
    "title": "实现 AgentErrorCode 到 HTTP 状态码映射",
    "description": "MapAgentErrorCode：403/404/429/400/503/500 分组映射",
    "status": "implemented",
    "source": "src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs:110-134"
  },
  {
    "id": "T-04",
    "title": "实现 ProblemDetails 构造方法",
    "description": "CreateProblemDetails：统一添加 traceId 和 timestamp 扩展字段，支持可变扩展参数",
    "status": "implemented",
    "source": "src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs:136-163"
  },
  {
    "id": "T-05",
    "title": "实现响应已开始时的重新抛出逻辑",
    "description": "若 context.Response.HasStarted，记录 LogWarning 并重新抛出异常",
    "status": "implemented",
    "source": "src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs:41-45"
  },
  {
    "id": "T-06",
    "title": "实现 SseErrorHandlerMiddleware",
    "description": "仅对 SSE 端点生效，捕获异常并以 SSE 事件格式输出错误",
    "status": "implemented",
    "source": "src/Host/Middleware/SseErrorHandlerMiddleware.cs:8-66"
  },
  {
    "id": "T-07",
    "title": "实现 SSE 端点检测",
    "description": "IsSseEndpoint：检查请求路径是否包含 /sse",
    "status": "implemented",
    "source": "src/Host/Middleware/SseErrorHandlerMiddleware.cs:37-40"
  },
  {
    "id": "T-08",
    "title": "实现 SSE 错误事件输出",
    "description": "HandleSseErrorAsync：写入 error 事件 + done 事件，跳过客户端中断",
    "status": "implemented",
    "source": "src/Host/Middleware/SseErrorHandlerMiddleware.cs:42-66"
  },
  {
    "id": "T-09",
    "title": "实现 SSE 响应头设置",
    "description": "响应未开始时设置 StatusCode=200, Content-Type=text/event-stream, Cache-Control=no-cache, Connection=keep-alive",
    "status": "implemented",
    "source": "src/Host/Middleware/SseErrorHandlerMiddleware.cs:51-57"
  },
  {
    "id": "T-10",
    "title": "实现 StreamingPayloadFactory.CreateErrorPayload",
    "description": "根据异常类型构造 StreamingErrorPayload：AgentException→{errorcode}, TimeoutException→timeout, 其他→internal-error",
    "status": "implemented",
    "source": "src/Host/StreamingPayloadFactory.cs:7-36"
  },
  {
    "id": "T-11",
    "title": "注册中间件",
    "description": "在 Program.cs 中按顺序注册 SseErrorHandlerMiddleware 和 GlobalExceptionHandlerMiddleware",
    "status": "implemented",
    "source": "src/Host/Program.cs:58-59"
  },
  {
    "id": "T-12",
    "title": "实现异常日志记录",
    "description": "GlobalExceptionHandlerMiddleware 记录 LogError（含 TraceId），响应已开始时 LogWarning；SseErrorHandlerMiddleware 记录 LogError",
    "status": "implemented",
    "source": "src/Host/Middleware/GlobalExceptionHandlerMiddleware.cs:39,43; src/Host/Middleware/SseErrorHandlerMiddleware.cs:44"
  }
]
```

## Tests


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

## Conventions


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
