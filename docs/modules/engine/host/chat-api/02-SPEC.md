# ChatApi - 详细规格

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
