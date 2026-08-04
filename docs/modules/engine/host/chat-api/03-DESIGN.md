# ChatApi - 设计文档

## 架构概览

ChatApi 模块采用 ASP.NET Core Minimal API 模式，所有端点通过 `EndpointExtensions.MapAgentEndpoints` 统一注册，挂载在 `/api/v1/agent` 路由组下并强制授权。

```
客户端请求
    │
    ▼
┌─────────────────────────────┐
│   ASP.NET Core Middleware    │
│  (Auth → SSE Error → Global)│
└─────────────┬───────────────┘
              │
              ▼
┌─────────────────────────────┐
│   EndpointExtensions         │
│   /api/v1/agent/*           │
│   RequireAuthorization()    │
└─────────────┬───────────────┘
              │
    ┌─────────┼──────────┐
    ▼         ▼          ▼
/chat  /chat/attachments[/stream]  /chat/stream  /chat/sse  /chat/pipeline  /agents
    │         │          │           │              │
    ▼         ▼          ▼           ▼              ▼
CreateAgentRequest()   CreateAgentRequest()  AgentRequest  IAgentConfigProvider
    │         │          │           │              │
    ▼         ▼          ▼           ▼              ▼
RequestScope  RequestScope  RequestScope  RequestScope    │
    │         │          │           │                    │
    ▼         ▼          ▼           ▼                    ▼
IAgentPipeline.ExecuteAsync  IAgentPipeline.ExecuteStreamAsync  ListAgentsAsync
```

## 关键文件

| 文件路径 | 职责 |
|----------|------|
| `src/Host/Extensions/EndpointExtensions.cs` | 端点注册、请求映射、用户上下文提取、流式输出 |
| `src/Host/Extensions/AttachmentEndpointExtensions.cs` | multipart 聊天端点与 `AgentRequest` 映射 |
| `src/Host/Attachments/AgentAttachmentReader.cs` | 附件数量、大小、扩展名、MIME 校验与受限读取 |
| `src/Host/Attachments/AgentAttachmentOptions.cs` | `Attachments` 配置节模型和默认限制 |
| `src/Engine/Services/RequestScope.cs` | 进行中请求的生命周期管理 |
| `src/Engine/Services/ShutdownService.cs` | 优雅关闭服务，管理 RequestScope 的注册与完成 |
| `src/Host/StreamingPayloadFactory.cs` | 流式事件（NDJSON/SSE）的载荷构造 |
| `src/Host/Program.cs` | 中间件注册顺序与端点映射入口 |

## 接口签名

### IAgentPipeline

```csharp
// 文件: Agent.Core/src/Core/Abstract/IAgentPipeline.cs
public interface IAgentPipeline
{
    Task<AgentResponse> ExecuteAsync(AgentRequest request, IAgentUserContext userContext, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ExecuteStreamAsync(AgentRequest request, IAgentUserContext userContext, CancellationToken cancellationToken);
}
```

### IAgentConfigProvider

```csharp
// 文件: Agent.Contracts/Configuration/IAgentConfigProvider.cs
public interface IAgentConfigProvider
{
    Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default);
}
```

### IAgentUserContext

```csharp
// 文件: Agent.Contracts/Security/AgentUserContext.cs
public interface IAgentUserContext
{
    string UserId { get; }
    string? TenantId { get; }
    IReadOnlyList<string> Groups { get; }
    IReadOnlyList<string> Roles { get; }
    IReadOnlyDictionary<string, string> Claims { get; }
    IReadOnlyList<string> Audience { get; }
    bool IsAuthenticated { get; }
}
```

## 数据依赖

### 输入模型

**ChatRequest**（`Agent.Contracts/Requests/ChatContracts.cs`）：
```csharp
public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Context { get; set; }
}
```

**AgentRequest**（`Agent.Contracts/Requests/AgentRequest.cs`）：
```csharp
public class AgentRequest
{
    public required string Query { get; init; }
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
    public string? TraceId { get; init; }
    public ClientType ClientType { get; init; } = ClientType.Web;
    public string? IdempotencyKey { get; init; }
    public Dictionary<string, string>? ExternalContext { get; init; }
    public List<string>? EnabledSkills { get; init; }
    public IReadOnlyList<AgentAttachment>? Attachments { get; init; }
}
```

### multipart 附件映射

```text
multipart/form-data
  message + agentId? + conversationId? + files[]
      -> AgentAttachmentReader 校验并读取
      -> AgentRequest.Attachments
      -> AgentPipeline / EngineChatMessage.Attachments
      -> 图片/PDF: MAF DataContent
      -> JSON/TXT/CSV/Markdown: MAF TextContent
```

Host 不持久化附件字节。`ExecutionInitializer` 生成的会话消息只记录文件名、MIME 和长度，避免 Base64/原始文件进入常规会话存储。

### 输出模型

**ChatResponse**（`Agent.Contracts/Requests/ChatContracts.cs`）：
```csharp
public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
}
```

**AgentResponse**（`Agent.Contracts/Requests/AgentResponse.cs`）：
```csharp
public class AgentResponse
{
    public required string Content { get; init; }
    public List<Citation>? Citations { get; init; }
    public List<ToolCallLog>? ToolCalls { get; init; }
    public TokenUsage? TokenUsage { get; init; }
    public string? TraceId { get; init; }
    public bool Success { get; init; } = true;
    public AgentErrorCode? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

**NdjsonStreamEvent**（`src/Host/StreamingPayloadFactory.cs`）：
```csharp
internal sealed class NdjsonStreamEvent
{
    public required string Type { get; init; }       // "content" | "error" | "done"
    public string? Content { get; init; }
    public string? Status { get; init; }             // "completed" | "cancelled" | "error"
    public string? TraceId { get; init; }
    public StreamingErrorPayload? Error { get; init; }
}
```

**StreamingErrorPayload**（`src/Host/StreamingPayloadFactory.cs`）：
```csharp
internal sealed class StreamingErrorPayload
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string TraceId { get; init; }
}
```

**AgentSummary**（`Agent.Contracts/Configuration/IAgentConfigProvider.cs`）：
```csharp
public sealed class AgentSummary
{
    public string AgentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Status { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
}
```

## 请求映射逻辑

### ChatRequest → AgentRequest（CreateAgentRequest）

```
AgentId        ← Context["agentId"] ?? X-Agent-Id Header
ConversationId ← Context["conversationId"] ?? X-Conversation-Id Header
TraceId        ← X-Trace-Id Header ?? Activity.Current.Id ?? context.TraceIdentifier
Query          ← ChatRequest.Message
ClientType     ← ClientType.Web（固定）
ExternalContext ← Context 中排除 agentId/conversationId/traceId 保留键后的键值对
```

### 用户上下文提取（ExtractUserContext）

```
UserId         ← context.User.Identity.Name ?? "anonymous"
TenantId       ← Claims["tenant_id"|"tid"] ?? X-Tenant-Id Header ?? X-TenantId Header
Roles          ← Claims[ClaimTypes.Role | "roles" | "role"]（去重）
Groups         ← Claims["groups" | "group"]（去重）
Claims         ← 所有 Claims 按 Type 分组，值逗号连接
Audience       ← HttpContext.Items["Audience"]（IEnumerable<string>|IEnumerable<object>） ?? X-Agent-Audience Header（逗号分隔，去重）
IsAuthenticated ← context.User.Identity.IsAuthenticated
```

## 中间件注册顺序

在 `Program.cs` 中的注册顺序：

1. `app.UseAgentHost()` — 基础设施中间件（CORS、JWT、健康检查等）
2. `app.UseMiddleware<SseErrorHandlerMiddleware>()` — SSE 端点错误处理
3. `app.UseMiddleware<GlobalExceptionHandlerMiddleware>()` — 全局异常处理
4. `app.MapControllers()`
5. `app.MapAgentEndpoints()` — ChatApi 端点

> [推断] SSE 错误处理器位于全局异常处理器之前，确保 SSE 端点的异常以 SSE 格式返回而非 ProblemDetails 格式。

## RequestScope 生命周期

```csharp
// 文件: src/Engine/Services/RequestScope.cs
internal class RequestScope : IDisposable
{
    public RequestScope(ShutdownService service, string requestType, string? traceId = null)
    {
        _requestId = service.RegisterRequest(requestType, traceId);
    }

    public void Dispose()
    {
        _service.CompleteRequest(_requestId);
    }
}
```

- 每个端点在处理请求时创建 `RequestScope`，向 `ShutdownService` 注册进行中请求
- 请求完成后（无论成功或异常），`RequestScope.Dispose()` 被调用，通知 `ShutdownService` 请求已完成
- `ShutdownService` 在应用关闭时等待所有进行中请求完成，实现优雅关闭
