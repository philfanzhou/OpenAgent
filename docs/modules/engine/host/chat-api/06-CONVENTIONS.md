# ChatApi - 约定规范

## 命名约定

### 端点命名

- 路由组基础路径：`/api/v1/agent`
- 子路径使用小写：`/chat`、`/chat/attachments`、`/chat/attachments/stream`、`/chat/stream`、`/chat/sse`、`/chat/pipeline`
- 端点名称（WithName）使用 PascalCase：`Chat`、`ChatWithAttachments`、`ChatWithAttachmentsStream`、`ChatStream`、`ChatSse`、`ChatPipeline`、`ListAgents`
- 标签统一使用 `"Agent"`

### HTTP Header 命名

- 使用 `X-` 前缀表示自定义 Header
- Header 名称使用 PascalCase 连字符格式：`X-Agent-Id`、`X-Conversation-Id`、`X-Trace-Id`、`X-Tenant-Id`、`X-Agent-Audience`
- TenantId Header 同时支持 `X-Tenant-Id` 和 `X-TenantId` 两种格式

### 方法命名

- 解析方法以 `Resolve` 为前缀：`ResolveAgentId`、`ResolveConversationId`、`ResolveTraceId`、`ResolveTenantId`
- 创建方法以 `Create` 为前缀：`CreateAgentRequest`、`CreateContentEvent`、`CreateErrorEvent`、`CreateDoneEvent`
- 提取方法以 `Extract` 为前缀：`ExtractUserContext`、`ExtractStringValue`
- 检查方法以 `Is` 为前缀：`IsReservedChatContextKey`
- 确保方法以 `Ensure` 为前缀：`EnsureSuccessfulResponse`
- 写入方法以 `Write` 为前缀：`WriteNdjsonEventAsync`

### 模型命名

- 请求模型：`ChatRequest`、`AgentRequest`
- 响应模型：`ChatResponse`、`AgentResponse`
- 流式事件模型：`NdjsonStreamEvent`、`StreamingErrorPayload`
- 用户上下文模型：`AgentUserContext`（实现 `IAgentUserContext` 接口）

## 流式协议约定

### NDJSON 协议

- Content-Type：`application/x-ndjson`
- 每行一个 JSON 对象，以 `\n` 结尾
- 每行写入后调用 `FlushAsync`
- 事件类型通过 `Type` 字段区分：`"content"`、`"error"`、`"done"`
- 完成状态通过 `Status` 字段表示：`"completed"`、`"cancelled"`、`"error"`

### SSE 协议

- Content-Type：`text/event-stream`
- Cache-Control：`no-cache`
- Connection：`keep-alive`
- 数据格式：`data: {json}\n\n`
- 完成信号：`data: [DONE]\n\n`
- 取消事件：`event: done\ndata: [CANCELLED]\n\n`
- 错误事件：`event: error\ndata: {json}\n\n` + `event: done\ndata: [ERROR]\n\n`

## 错误处理约定

### 同步端点

- `response.Success` 为 false 时抛出 `AgentException`，由 `GlobalExceptionHandlerMiddleware` 统一处理为 ProblemDetails
- 异常不会在端点内部捕获，向上传播至中间件层

### 流式端点

- 异常在端点内部捕获，以流式事件格式输出
- `OperationCanceledException`：输出取消事件（非客户端中断时）
  - NDJSON 端点条件：`!context.RequestAborted.IsCancellationRequested`
  - SSE 端点条件：`!context.Response.HasStarted || !context.RequestAborted.IsCancellationRequested`（响应尚未开始时即使客户端中断也会写入）
- 其他异常：输出 error 事件 + done 事件（非客户端中断时）
- 客户端中断（`context.RequestAborted.IsCancellationRequested`）时不写入任何事件（SSE 端点在响应尚未开始时除外）

## 请求解析优先级约定

### multipart 附件

- 文本字段使用 `message`、`agentId`、`conversationId`，文件字段使用 `files`
- 文件名必须经过 `Path.GetFileName`，不保留客户端路径
- MIME 去除参数后比较；扩展名、MIME 均需允许且必须互相匹配
- 默认允许图片、PDF、JSON、纯文本、CSV 和 Markdown；部署可在 `Attachments` 配置节收紧
- `AgentRequest.Attachments` 使用 `JsonIgnore`，附件字节不能从 `/chat/pipeline` JSON 入口传入或意外序列化输出
- 同步端点返回 `ChatResponse`；`/chat/attachments/stream` 返回与普通 NDJSON 端点相同的事件结构

### AgentId

1. `ChatRequest.Context["agentId"]`
2. `X-Agent-Id` Header

### ConversationId

1. `ChatRequest.Context["conversationId"]`
2. `X-Conversation-Id` Header

### TraceId

1. `X-Trace-Id` Header
2. `Activity.Current.Id`
3. `context.TraceIdentifier`（最终回退）

### TenantId

1. Claims（`tenant_id` 或 `tid`）
2. `X-Tenant-Id` Header
3. `X-TenantId` Header

### Audience

1. `HttpContext.Items["Audience"]`（`IEnumerable<string>` 或 `IEnumerable<object>`）
2. `X-Agent-Audience` Header（逗号分隔，去重）

## 日志约定

> [推断] 当前代码中端点层未直接记录日志，异常日志由 `GlobalExceptionHandlerMiddleware` 和 `SseErrorHandlerMiddleware` 统一记录。

- `GlobalExceptionHandlerMiddleware`：`LogError("Unhandled exception occurred. TraceId: {TraceId}")`，响应已开始时 `LogWarning`
- `SseErrorHandlerMiddleware`：`LogError("SSE endpoint error occurred")`

## 序列化约定

- NDJSON 事件使用 `System.Text.Json.JsonSerializer.Serialize` 默认序列化（PascalCase 属性名）
- SSE 数据使用 `JsonSerializer.Serialize` 默认序列化
- `GlobalExceptionHandlerMiddleware` 使用 `JsonNamingPolicy.CamelCase` 和 `JsonIgnoreCondition.WhenWritingNull`

## 授权约定

- 所有端点通过路由组级别 `RequireAuthorization()` 统一保护
- 用户上下文中的 `IsAuthenticated` 字段反映实际认证状态
- 未认证请求由 ASP.NET Core 中间件层拦截返回 401
- 路由授权只是第一层；进入 pipeline 后仍使用 `IAgentAuthorizationService` 对 Agent、Model、Tool、Function、MCP 和 Skill 执行资源级授权
