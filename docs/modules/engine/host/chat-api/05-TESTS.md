# ChatApi - 测试文档

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
