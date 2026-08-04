# ChatApi - 任务清单

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
