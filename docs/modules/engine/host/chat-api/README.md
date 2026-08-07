# Chat API（Engine Host）

ChatApi 提供 Agent.Engine 的核心 HTTP 端点，支持客户端以同步、流式或 multipart 方式与 AI Agent 对话。

## 端点

| 端点 | 方法 | 说明 | 响应类型 |
|------|------|------|----------|
| `/api/v1/agent/chat` | POST | 同步聊天 | JSON |
| `/api/v1/agent/chat/stream` | POST | SSE 流式 | text/event-stream |
| `/api/v1/agent/chat/attachments` | POST | 带附件同步 | JSON |
| `/api/v1/agent/chat/attachments/stream` | POST | 带附件 SSE 流式 | text/event-stream |
| `/api/v1/agent/agents` | GET | Agent 列表 | JSON |
| `/api/v1/agent/conversations` | GET | 会话列表 | JSON |
| `/api/v1/agent/conversations/search` | GET | 会话搜索 | JSON |
| `/api/v1/agent/conversations/{conversationId}` | GET | 会话详情 | JSON |
| `/api/v1/agent/conversations/{conversationId}` | DELETE | 软删除会话 | 空响应 |
| `/health`、`/health/live` | GET | 存活检查 | Health Check |
| `/ready`、`/health/ready` | GET | 就绪检查 | Health Check |
| `/metrics` | GET | Prometheus 指标 | Text |

## 核心能力

- **流式响应**：SSE 事件流
- **多模态输入**：图片/PDF/文本附件映射到 `AgentRequest.Attachments`
- **上传防护**：数量、大小、MIME 类型校验
- **请求追踪**：Header / Activity 自动生成 TraceId
- **优雅中断**：客户端断开时正确释放资源

## 当前状态

**已实现** — 所有端点均已落地。

## 源码位置

- 端点组合：`Backend/src/OpenAgent.Engine.Host/Extensions/EndpointExtensions.cs`
- 聊天端点：`Backend/src/OpenAgent.Engine.Host/Extensions/AgentChatEndpointExtensions.cs`
- 会话端点：`Backend/src/OpenAgent.Engine.Host/Extensions/ConversationEndpointExtensions.cs`
- 附件端点：`Backend/src/OpenAgent.Engine.Host/Extensions/AttachmentEndpointExtensions.cs`
- 流式响应：`Backend/src/OpenAgent.Engine.Host/Extensions/AgentStreamWriter.cs`
- 中间件：`Backend/src/OpenAgent.Engine.Host/Middleware/`
- 流式处理：`Backend/src/OpenAgent.Engine.Host/StreamingPayloadFactory.cs`
