# ErrorHandling - 任务清单

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
