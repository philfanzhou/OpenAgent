
## Feature


## 核心用户故事

作为客户端应用，我希望向 Engine 发送聊天请求并获取同步或流式响应，以便集成 AI 对话能力。

## 功能简介

ChatApi 模块提供了 Agent.Engine 的核心 HTTP 端点，允许客户端应用以同步、流式或 multipart 方式与 AI Agent 进行对话交互。模块支持两种流式传输协议（NDJSON、SSE）、图片/文件附件和原始管道请求，并提供 Agent 列表查询能力。

## 端点一览

| 端点 | 方法 | 说明 | 响应类型 |
|------|------|------|----------|
| `/api/v1/agent/chat` | POST | 同步聊天请求 | JSON |
| `/api/v1/agent/chat/attachments` | POST | 带图片/文件的 multipart 聊天 | JSON |
| `/api/v1/agent/chat/attachments/stream` | POST | 带图片/文件的 multipart 流式聊天 | application/x-ndjson |
| `/api/v1/agent/chat/stream` | POST | NDJSON 流式聊天 | application/x-ndjson |
| `/api/v1/agent/chat/sse` | POST | SSE 流式聊天 | text/event-stream |
| `/api/v1/agent/chat/pipeline` | POST | 原始管道请求 | JSON |
| `/api/v1/agent/agents` | GET | 获取 Agent 列表 | JSON |

## 关键能力

- **多协议流式传输**：支持 NDJSON 和 SSE 两种流式协议，适配不同客户端需求
- **多模态输入**：受限的 multipart 入口将图片、PDF 和文本类文件映射到 `AgentRequest.Attachments`
- **上传防护**：按数量、单文件/总大小、扩展名和 MIME 类型拒绝不合规附件
- **用户上下文提取**：从 HTTP 请求中自动提取用户身份、租户、角色、分组等信息
- **请求追踪**：支持通过 Header 或 Activity 自动生成 TraceId
- **优雅中断处理**：流式端点在客户端断开或操作取消时正确处理资源释放
- **请求生命周期管理**：通过 RequestScope 跟踪进行中的请求，支持优雅关闭

## Specification


## 功能需求 (FR)

### FR-01: 同步聊天端点

- **FR-01.1**: 端点路径为 `POST /api/v1/agent/chat`，接受 `ChatRequest` 请求体
- **FR-01.2**: 将 `ChatRequest.Message` 映射为 `AgentRequest.Query`
- **FR-01.3**: 从 `ChatRequest.Context["agentId"]` 或 `X-Agent-Id` Header 解析 AgentId
- **FR-01.4**: 从 `ChatRequest.Context["conversationId"]` 或 `X-Conversation-Id` Header 解析 ConversationId
- **FR-01.5**: 从 `X-Trace-Id` Header 或 `Activity.Current.Id` 或 `context.TraceIdentifier` 解析 TraceId
- **FR-01.6**: 固定设置 `ClientType = Web`
- **FR-01.7**: 过滤 `ChatRequest.Context` 中的保留键（agentId、conversationId、traceId），将其余键值对映射为 `AgentRequest.ExternalContext`
- **FR-01.8**: 使用 `RequestScope` 注册进行中请求
- **FR-01.9**: 调用 `IAgentPipeline.ExecuteAsync` 执行请求
- **FR-01.10**: 返回 `ChatResponse { Message = response.Content }`
- **FR-01.11**: 若 `response.Success` 为 false，抛出 `AgentException`

### FR-02: NDJSON 流式聊天端点

- **FR-02.1**: 端点路径为 `POST /api/v1/agent/chat/stream`
- **FR-02.2**: 输入映射与 FR-01 相同
- **FR-02.3**: 响应 Content-Type 为 `application/x-ndjson`，Cache-Control 为 `no-cache`
- **FR-02.4**: 迭代 `pipeline.ExecuteStreamAsync`，每个 chunk 写入 `NdjsonStreamEvent`（Type="content"）
- **FR-02.5**: 流结束时写入 done 事件（Type="done"，Status="completed"）
- **FR-02.6**: `OperationCanceledException`：若非客户端中断，写入 done 事件（Status="cancelled"）
- **FR-02.7**: 其他异常：若非客户端中断，写入 error 事件 + done 事件（Status="error"）

### FR-03: SSE 流式聊天端点

- **FR-03.1**: 端点路径为 `POST /api/v1/agent/chat/sse`
- **FR-03.2**: 输入映射与 FR-01 相同
- **FR-03.3**: 响应 Content-Type 为 `text/event-stream`，Cache-Control 为 `no-cache`，Connection 为 `keep-alive`
- **FR-03.4**: 每个 chunk 写入 `data: {content_json}\n\n`
- **FR-03.5**: 流结束时写入 `data: [DONE]\n\n`
- **FR-03.6**: `OperationCanceledException`：若非客户端中断（或响应尚未开始），写入 `event: done\ndata: [CANCELLED]\n\n`
- **FR-03.7**: 其他异常：若非客户端中断，写入 `event: error\ndata: {error_json}\n\n` + `event: done\ndata: [ERROR]\n\n`

### FR-04: 原始管道端点

- **FR-04.1**: 端点路径为 `POST /api/v1/agent/chat/pipeline`，接受 `AgentRequest` 请求体
- **FR-04.2**: 从请求体或 `X-Agent-Id` Header 解析 AgentId
- **FR-04.3**: 从请求体或 `X-Conversation-Id` Header 解析 ConversationId
- **FR-04.4**: 从请求体或 `X-Trace-Id` Header 或 `Activity.Current.Id` 或 `context.TraceIdentifier` 解析 TraceId
- **FR-04.5**: 返回原始 `AgentResponse` 对象

### FR-05: Agent 列表端点

- **FR-05.1**: 端点路径为 `GET /api/v1/agent/agents`
- **FR-05.2**: 调用 `IAgentConfigProvider.ListAgentsAsync`
- **FR-05.3**: 返回 `AgentSummary` 列表

### FR-06: 用户上下文提取

- **FR-06.1**: UserId 从 `context.User.Identity.Name` 提取，默认 "anonymous"
- **FR-06.2**: TenantId 从 `tenant_id`/`tid` Claim 或 `X-Tenant-Id`/`X-TenantId` Header 提取
- **FR-06.3**: Roles 从 `ClaimTypes.Role` 或 `roles`/`role` Claim 提取
- **FR-06.4**: Groups 从 `groups`/`group` Claim 提取
- **FR-06.5**: Claims 按 Type 分组，值为逗号连接
- **FR-06.6**: Audience 从 `HttpContext.Items["Audience"]` 或 `X-Agent-Audience` Header（逗号分隔）提取
- **FR-06.7**: IsAuthenticated 从 `context.User.Identity.IsAuthenticated` 提取

### FR-07: 端点授权

- **FR-07.1**: 所有端点注册在 `/api/v1/agent` 路由组下，调用 `RequireAuthorization()`

### FR-08: 附件聊天端点

- **FR-08.1**: 端点路径为 `POST /api/v1/agent/chat/attachments`，接受 `multipart/form-data`
- **FR-08.2**: 表单必须包含非空 `message` 和至少一个 `files` 文件，可包含 `agentId` 和 `conversationId`
- **FR-08.3**: `agentId` 和 `conversationId` 优先使用表单字段，缺失时回退到对应 Header
- **FR-08.4**: 默认最多 5 个文件、单文件 10 MiB、总计 25 MiB；可通过 `Attachments` 配置节收紧
- **FR-08.5**: 同时校验扩展名和客户端声明的 MIME，拒绝空文件和超限数据
- **FR-08.5a**: 扩展名必须与 MIME 类型匹配，例如 `.png` 不能声明为 `text/plain`
- **FR-08.6**: 读取后构造 `AgentAttachment { FileName, MediaType, Data }`，随 `AgentRequest` 进入管道
- **FR-08.7**: 端点返回 `ChatResponse`；附件字节只在当前请求中传递，会话仅保存元数据
- **FR-08.8**: `AgentRequest.Attachments` 不参与 JSON 序列化/反序列化，`/chat/pipeline` 不能用 Base64 JSON 绕过 multipart 限制
- **FR-08.9**: `POST /api/v1/agent/chat/attachments/stream` 接受同一 multipart 请求并以 NDJSON 输出 content、error 和 done 事件

## 验收标准 (AC)

### AC-01: 健康检查路径映射

> 来源：`test/OpenAgent.Engine.Tests/Hosting/HostingTests.cs`

- **AC-01.1**: `UseAgentHost` 注册 `/health` 路径
- **AC-01.2**: `UseAgentHost` 注册 `/ready` 路径
- **AC-01.3**: `UseAgentHost` 注册 `/health/live` 路径
- **AC-01.4**: `UseAgentHost` 注册 `/health/ready` 路径

### AC-02 ~ AC-07: 端点功能验收

- **[当前无测试覆盖]** 同步聊天端点的请求映射与响应
- **[当前无测试覆盖]** NDJSON 流式端点的流式输出与异常处理
- **[当前无测试覆盖]** SSE 流式端点的流式输出与异常处理
- **[当前无测试覆盖]** 原始管道端点的请求映射与响应
- **[当前无测试覆盖]** Agent 列表端点的返回结果
- **[当前无测试覆盖]** 用户上下文提取逻辑
- **[已覆盖]** 附件读取器覆盖合法图片、空文件、非法扩展名、超大文件、超数量和扩展名/MIME 不匹配；TestEnv 覆盖图片同步、文本 NDJSON 流式和错误请求不调用模型

## 数据模型

### ChatRequest

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| Message | string | 是 | 用户消息内容 |
| Context | Dictionary\<string, object\>? | 否 | 上下文字典，可包含 agentId、conversationId 等保留键及其他自定义键值 |

### ChatResponse

| 字段 | 类型 | 说明 |
|------|------|------|
| Message | string | Agent 响应内容 |

### AgentAttachment

| 字段 | 类型 | 说明 |
|------|------|------|
| FileName | string | 清理路径后的文件名 |
| MediaType | string | 规范化后的 MIME 类型 |
| Data | byte[] | 当前请求内使用的文件字节 |

### NdjsonStreamEvent

| 字段 | 类型 | 说明 |
|------|------|------|
| Type | string | 事件类型："content"、"error"、"done" |
| Content | string? | 内容（Type="content" 时） |
| Status | string? | 状态（Type="done" 时）："completed"、"cancelled"、"error" |
| TraceId | string? | 追踪标识 |
| Error | StreamingErrorPayload? | 错误详情（Type="error" 时） |

### AgentSummary

| 字段 | 类型 | 说明 |
|------|------|------|
| AgentId | string | Agent 标识 |
| Name | string | Agent 名称 |
| Status | int | Agent 状态 |
| CurrentVersion | string | 当前版本 |
| Framework | string | 使用的框架 |

## Design


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

## Tasks


```json
[
  {
    "id": "T-01",
    "title": "注册 /api/v1/agent 路由组并启用授权",
    "description": "在 EndpointExtensions.MapAgentEndpoints 中创建路由组，调用 RequireAuthorization()",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:20-23"
  },
  {
    "id": "T-02",
    "title": "实现 POST /chat 同步聊天端点",
    "description": "接收 ChatRequest，映射为 AgentRequest，调用 pipeline.ExecuteAsync，返回 ChatResponse",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:25-47"
  },
  {
    "id": "T-03",
    "title": "实现 POST /chat/stream NDJSON 流式端点",
    "description": "接收 ChatRequest，调用 pipeline.ExecuteStreamAsync，以 NDJSON 格式输出流式事件",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:49-114"
  },
  {
    "id": "T-04",
    "title": "实现 POST /chat/sse SSE 流式端点",
    "description": "接收 ChatRequest，调用 pipeline.ExecuteStreamAsync，以 SSE 格式输出流式事件",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:116-162"
  },
  {
    "id": "T-05",
    "title": "实现 POST /chat/pipeline 原始管道端点",
    "description": "接收 AgentRequest，调用 pipeline.ExecuteAsync，返回原始 AgentResponse",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:164-189"
  },
  {
    "id": "T-06",
    "title": "实现 GET /agents Agent 列表端点",
    "description": "调用 IAgentConfigProvider.ListAgentsAsync，返回 AgentSummary 列表",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:191-199"
  },
  {
    "id": "T-07",
    "title": "实现 ChatRequest 到 AgentRequest 的映射",
    "description": "CreateAgentRequest 方法：Message→Query，Context 保留键解析，Header 回退",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:214-229"
  },
  {
    "id": "T-08",
    "title": "实现用户上下文提取",
    "description": "ExtractUserContext 方法：从 HttpContext 提取 UserId、TenantId、Roles、Groups、Claims、Audience、IsAuthenticated",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:287-323"
  },
  {
    "id": "T-09",
    "title": "实现 AgentId 解析逻辑",
    "description": "ResolveAgentId：优先从 Context 字典提取，回退到 X-Agent-Id Header",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:243-250"
  },
  {
    "id": "T-10",
    "title": "实现 ConversationId 解析逻辑",
    "description": "ResolveConversationId：优先从 Context 字典提取，回退到 X-Conversation-Id Header",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:252-259"
  },
  {
    "id": "T-11",
    "title": "实现 TraceId 解析逻辑",
    "description": "ResolveTraceId：X-Trace-Id Header → Activity.Current.Id → context.TraceIdentifier",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:261-266"
  },
  {
    "id": "T-12",
    "title": "实现 TenantId 解析逻辑",
    "description": "ResolveTenantId：Claims[tenant_id|tid] → X-Tenant-Id Header → X-TenantId Header",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:325-329"
  },
  {
    "id": "T-13",
    "title": "实现 Audience 解析逻辑",
    "description": "ResolveAudience：HttpContext.Items[Audience] → X-Agent-Audience Header（逗号分隔）",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:332-359"
  },
  {
    "id": "T-14",
    "title": "实现保留键过滤逻辑",
    "description": "IsReservedChatContextKey：过滤 agentId、conversationId、traceId 键",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:280-285"
  },
  {
    "id": "T-15",
    "title": "实现流式载荷工厂",
    "description": "StreamingPayloadFactory：CreateContentEvent、CreateErrorEvent、CreateDoneEvent",
    "status": "implemented",
    "source": "src/Host/StreamingPayloadFactory.cs:38-66"
  },
  {
    "id": "T-16",
    "title": "实现 NDJSON 写入辅助方法",
    "description": "WriteNdjsonEventAsync：序列化 NdjsonStreamEvent 并写入响应流",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:204-212"
  },
  {
    "id": "T-17",
    "title": "实现请求成功性检查",
    "description": "EnsureSuccessfulResponse：若 response.Success 为 false，抛出 AgentException",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:231-241"
  },
  {
    "id": "T-18",
    "title": "实现 RequestScope 进行中请求跟踪",
    "description": "RequestScope：注册/完成请求到 ShutdownService，支持优雅关闭",
    "status": "implemented",
    "source": "src/Engine/Services/RequestScope.cs"
  },
  {
    "id": "T-19",
    "title": "注册中间件与端点映射",
    "description": "在 Program.cs 中注册 SseErrorHandlerMiddleware、GlobalExceptionHandlerMiddleware，调用 MapAgentEndpoints",
    "status": "implemented",
    "source": "src/Host/Program.cs:57-61"
  },
  {
    "id": "T-20",
    "title": "实现 AgentId 写入 HttpContext.Items",
    "description": "在 /chat 和 /chat/stream 端点中，将解析后的 AgentId 写入 HttpContext.Items[\"AgentId\"]",
    "status": "implemented",
    "source": "src/Host/Extensions/EndpointExtensions.cs:36-39,59-63"
  },
  {
    "id": "T-21",
    "title": "实现 POST /chat/attachments multipart 端点",
    "description": "接收 message、agentId、conversationId 和 files，构造带 Attachments 的 AgentRequest",
    "status": "implemented",
    "source": "src/Host/Extensions/AttachmentEndpointExtensions.cs"
  },
  {
    "id": "T-22",
    "title": "实现附件安全限制",
    "description": "校验文件数量、单文件/总大小、扩展名、MIME 和空文件",
    "status": "implemented",
    "source": "src/Host/Attachments/AgentAttachmentReader.cs"
  },
  {
    "id": "T-23",
    "title": "实现 POST /chat/attachments/stream multipart 流式端点",
    "description": "复用受限附件读取和用户上下文，以 NDJSON 输出流式内容、遥测、错误与完成事件",
    "status": "implemented",
    "source": "src/Host/Extensions/AttachmentEndpointExtensions.cs"
  },
  {
    "id": "T-24",
    "title": "补充部署级文件内容检测",
    "description": "按部署需要增加 magic-byte、恶意文件扫描和租户对象存储",
    "status": "planned",
    "source": "Todo/2026-08-03-engine-core-agent-runtime-redesign.md#04-对外-api"
  }
]
```

## Tests


## 现有测试

### HostingTests - 健康检查路径映射

**文件**: `test/OpenAgent.Engine.Tests/Hosting/HostingTests.cs`

#### TC-01: UseAgentHost_MapsLegacyHealthCheckAliases

- **Given** 创建了 WebApplication 并配置了路由、健康检查和 AgentHostOptions（禁用 CORS/Swagger/JWT）
- **When** 调用 `app.UseAgentHost(configuration)`
- **Then** 路由模式中包含 `/health`、`/ready`、`/health/live`、`/health/ready`

### AgentAttachmentReaderTests - 附件校验

**文件**: `test/OpenAgent.Engine.Tests/Hosting/AgentAttachmentReaderTests.cs`

- **TC-02**: 合法 PNG 被读取为文件名、MIME 和字节一致的 `AgentAttachment`
- **TC-03**: 空文件、不允许的 `.exe` 和超大文件返回 `InvalidRequest`
- **TC-04**: 超过 `MaxFileCount` 的附件集合返回 `InvalidRequest`
- **TC-05**: 扩展名和 MIME 不匹配返回 `InvalidRequest`

### AttachmentChatTests - multipart 端点

**文件**: `TestCode/Agent.TestEngine/AttachmentChatTests.cs`

- **TC-06**: PNG 同步上传进入 MAF 并返回模型响应
- **TC-07**: TXT 上传从附件流端点返回 NDJSON content/done
- **TC-08**: 扩展名/MIME 不匹配在模型调用前拒绝

---

## 缺失测试场景

### 附件聊天端点 (POST /chat/attachments 与 /chat/attachments/stream)

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-A01 | Given 合法 multipart 图片，When 调用端点，Then pipeline 收到完整 `AgentAttachment` | 高 |
| MT-A02 | Given 缺失 message 或 files，When 调用端点，Then 返回结构化 4xx | 高 |
| MT-A03 | Given 文件扩展名与 MIME 不一致，When 调用端点，Then 拒绝请求 | 高 |
| MT-A04 | Given 合法 PDF/文本，When 选用支持的模型，Then MAF 收到对应 `DataContent`/`TextContent` | 高 |
| MT-A05 | Given 合法 multipart，When 调用附件流端点，Then 输出 NDJSON content 和 done | 高 |

### 同步聊天端点 (POST /chat)

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-01 | Given 有效的 ChatRequest，When 调用 /chat，Then 返回 200 和 ChatResponse | 高 |
| MT-02 | Given ChatRequest 包含 Context["agentId"]，When 调用 /chat，Then AgentRequest.AgentId 使用 Context 中的值 | 高 |
| MT-03 | Given ChatRequest 不含 Context["agentId"] 但有 X-Agent-Id Header，When 调用 /chat，Then AgentRequest.AgentId 使用 Header 值 | 高 |
| MT-04 | Given ChatRequest 包含 Context["conversationId"]，When 调用 /chat，Then AgentRequest.ConversationId 使用 Context 中的值 | 高 |
| MT-05 | Given 无 X-Trace-Id Header 但有 Activity.Current.Id，When 调用 /chat，Then TraceId 使用 Activity.Current.Id | 中 |
| MT-06 | Given pipeline 返回 Success=false 的 AgentResponse，When 调用 /chat，Then 抛出 AgentException | 高 |
| MT-07 | Given ChatRequest.Context 包含保留键和自定义键，When 调用 /chat，Then ExternalContext 仅包含自定义键 | 中 |
| MT-08 | Given 未认证请求，When 调用 /chat，Then 返回 401 | 高 |

### NDJSON 流式端点 (POST /chat/stream)

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-09 | Given 有效的 ChatRequest，When 调用 /chat/stream，Then 返回 Content-Type 为 application/x-ndjson | 高 |
| MT-10 | Given pipeline 返回多个 chunk，When 调用 /chat/stream，Then 每个 chunk 输出 Type=content 的 NdjsonStreamEvent | 高 |
| MT-11 | Given 流正常完成，When 调用 /chat/stream，Then 最后输出 Type=done, Status=completed 的事件 | 高 |
| MT-12 | Given pipeline 抛出 OperationCanceledException 且非客户端中断，When 调用 /chat/stream，Then 输出 Type=done, Status=cancelled 的事件 | 高 |
| MT-13 | Given pipeline 抛出普通异常且非客户端中断，When 调用 /chat/stream，Then 输出 error 事件 + Type=done, Status=error 的事件 | 高 |
| MT-14 | Given 客户端中断请求，When 调用 /chat/stream，Then 不写入额外事件 | 中 |

### SSE 流式端点 (POST /chat/sse)

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-15 | Given 有效的 ChatRequest，When 调用 /chat/sse，Then 返回 Content-Type 为 text/event-stream | 高 |
| MT-16 | Given pipeline 返回多个 chunk，When 调用 /chat/sse，Then 每个 chunk 输出 `data: {json}\n\n` 格式 | 高 |
| MT-17 | Given 流正常完成，When 调用 /chat/sse，Then 输出 `data: [DONE]\n\n` | 高 |
| MT-18 | Given pipeline 抛出 OperationCanceledException，When 调用 /chat/sse，Then 输出 `event: done\ndata: [CANCELLED]\n\n` | 高 |
| MT-19 | Given pipeline 抛出普通异常，When 调用 /chat/sse，Then 输出 `event: error\ndata: {json}\n\n` + `event: done\ndata: [ERROR]\n\n` | 高 |

### 原始管道端点 (POST /chat/pipeline)

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-20 | Given 有效的 AgentRequest，When 调用 /chat/pipeline，Then 返回原始 AgentResponse | 高 |
| MT-21 | Given AgentRequest.AgentId 为空但有 X-Agent-Id Header，When 调用 /chat/pipeline，Then 使用 Header 值 | 中 |
| MT-22 | Given pipeline 返回 Success=false 的 AgentResponse，When 调用 /chat/pipeline，Then 抛出 AgentException | 高 |

### Agent 列表端点 (GET /agents)

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-23 | Given IAgentConfigProvider 返回 Agent 列表，When 调用 /agents，Then 返回 200 和列表数据 | 高 |

### 用户上下文提取

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-24 | Given 已认证用户，When 提取用户上下文，Then UserId 为用户名 | 高 |
| MT-25 | Given 未认证用户，When 提取用户上下文，Then UserId 为 "anonymous" | 高 |
| MT-26 | Given Claims 包含 tenant_id，When 提取 TenantId，Then 使用 Claim 值 | 中 |
| MT-27 | Given 无 Tenant Claim 但有 X-Tenant-Id Header，When 提取 TenantId，Then 使用 Header 值 | 中 |
| MT-28 | Given HttpContext.Items 包含 Audience，When 提取 Audience，Then 使用 Items 中的值 | 中 |
| MT-29 | Given 无 Items Audience 但有 X-Agent-Audience Header（逗号分隔），When 提取 Audience，Then 解析为列表 | 中 |

### RequestScope

| 编号 | 场景 | 优先级 |
|------|------|--------|
| MT-30 | Given 请求处理中，When RequestScope 创建，Then ShutdownService 注册了进行中请求 | 中 |
| MT-31 | Given 请求处理完成，When RequestScope Dispose，Then ShutdownService 标记请求完成 | 中 |

## Conventions


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
