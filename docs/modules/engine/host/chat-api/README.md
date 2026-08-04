# Chat API（Engine Host）

ChatApi 提供 Agent.Engine 的核心 HTTP 端点，支持客户端以同步、流式或 multipart 方式与 AI Agent 对话。

## 端点

| 端点 | 方法 | 说明 | 响应类型 |
|------|------|------|----------|
| `/api/v1/agent/chat` | POST | 同步聊天 | JSON |
| `/api/v1/agent/chat/stream` | POST | NDJSON 流式 | application/x-ndjson |
| `/api/v1/agent/chat/sse` | POST | SSE 流式 | text/event-stream |
| `/api/v1/agent/chat/attachments` | POST | 带附件同步 | JSON |
| `/api/v1/agent/chat/attachments/stream` | POST | 带附件流式 | application/x-ndjson |
| `/api/v1/agent/chat/pipeline` | POST | 原始管道请求 | JSON |
| `/api/v1/agent/agents` | GET | Agent 列表 | JSON |

## 核心能力

- **多协议流式**：NDJSON + SSE 两种流式协议
- **多模态输入**：图片/PDF/文本附件映射到 `AgentRequest.Attachments`
- **上传防护**：数量、大小、MIME 类型校验
- **请求追踪**：Header / Activity 自动生成 TraceId
- **优雅中断**：客户端断开时正确释放资源

## 当前状态

**已实现** — 所有端点均已落地。

## 源码位置

- 端点定义：`Backend/src/OpenAgent.Engine.Host/Extensions/EndpointExtensions.cs`
- 中间件：`Backend/src/OpenAgent.Engine.Host/Middleware/`
- 流式处理：`Backend/src/OpenAgent.Engine.Host/StreamingPayloadFactory.cs`
