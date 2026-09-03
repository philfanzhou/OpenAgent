# Chat API（Engine Host）

ChatApi 提供 Agent.Engine 的核心 HTTP 端点；聊天使用 JSON，文件须先独立上传并以 `fileIds` 引用。

## 端点

| 端点 | 方法 | 说明 | 响应类型 |
|------|------|------|----------|
| `/api/v1/agent/chat` | POST | 同步聊天 | JSON |
| `/api/v1/agent/chat/stream` | POST | SSE 流式 | text/event-stream |
| `/api/v1/agent/files` | POST | 独立上传文件资产 | JSON |
| `/api/v1/agent/files/{fileId}/content` | GET | 文件预览内容 | 原始 MIME |
| `/api/v1/agent/files/{fileId}/download` | GET | 下载文件资产 | 原始 MIME |
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
- **多模态输入**：聊天以 `fileIds` 引用已上传文件，执行时按需读取，不在会话中保存字节。
- **MCP 跨系统传输 / 用户分享链接**：大模型调用 `create_file_transfer_url` 返回短期签名 URL——既可传给需要文件 URL 的第三方 MCP，也可作为临时下载链接交给用户（须告知 `expiresAt` 有效期）；`objectKey` 是实际 S3 对象键，不是 S3 ID。
- **上传防护**：数量、大小、MIME 类型校验
- **请求追踪**：Header / Activity 自动生成 TraceId
- **优雅中断**：客户端断开时正确释放资源

## Token usage 契约

非流式 `/chat` 响应在原有 `message` 外增加可选 `usage` 和 `modelId`。流式 `/chat/stream` 不发送独立 usage 事件，而是在终态事件中返回：

```text
event: done
data: {"done":true,"usage":{"promptTokens":21,"completionTokens":8,"totalTokens":29},"modelId":"provider-model","conversationId":"..."}
```

`usage` 为 `null` 表示 Provider 未返回完整统计，客户端不得将其解释为 0。可选的 `cachedInputTokens`、`reasoningTokens` 是细分项，不额外计入 total。旧客户端可忽略新增字段，原有 `message`、content/reasoning/tool_call/done 事件名称保持不变。

## 当前状态

**已实现** — 所有端点均已落地。

## 源码位置

- 端点组合：`Backend/src/OpenAgent.Engine.Host/Extensions/EndpointExtensions.cs`
- 聊天端点：`Backend/src/OpenAgent.Engine.Host/Extensions/AgentChatEndpointExtensions.cs`
- 会话端点：`Backend/src/OpenAgent.Engine.Host/Extensions/ConversationEndpointExtensions.cs`
- 文件端点：`Backend/src/OpenAgent.Engine.Host/Extensions/FileAssetEndpointExtensions.cs`
- 流式响应：`Backend/src/OpenAgent.Engine.Host/Extensions/AgentStreamWriter.cs`
- 中间件：`Backend/src/OpenAgent.Engine.Host/Middleware/`
- 流式处理：`Backend/src/OpenAgent.Engine.Host/StreamingPayloadFactory.cs`
