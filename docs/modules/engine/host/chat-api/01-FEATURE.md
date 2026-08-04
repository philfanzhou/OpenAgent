# ChatApi - 功能概述

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

## 相关文档

- [02-SPEC - 详细规格](./02-SPEC.md)
- [03-DESIGN - 设计文档](./03-DESIGN.md)
- [04-TASKS - 任务清单](./04-TASKS.md)
- [05-TESTS - 测试文档](./05-TESTS.md)
- [06-CONVENTIONS - 约定规范](./06-CONVENTIONS.md)
