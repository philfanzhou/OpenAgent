# LLM Provider

模型推理与自动压缩统一由 ACP 对端 OpenCode 按运行时配置执行。平台只解析、授权并传递模型配置，不创建模型 SDK 客户端或单独调用摘要模型。连接格式包括 OpenAI Chat Completions、OpenAI Responses 与 Anthropic Messages，由运行时适配。

## 配置与选择

`LlmProviderProfile` 是租户级独立资源，保存：

- Model ID
- Modality (`Text` 或 `Multimodal`；当前多模态只支持图片)
- Context Tokens (`ContextTokens`，HTTP JSON 为 `contextTokens`)
- API Format
- Endpoint
- Temperature
- API Key（服务端租户绑定加密存储）

Agent 不再绑定 Provider 或 Model。执行请求携带 `llmProfileId`，`AgentRuntimeResolver` 按已验证的 tenantId 分别加载 Agent 与 LLM Profile，完成资源授权后创建本次执行的 `LlmConfig`。平台将 LLM 的 `ContextTokens` 传给运行时；压缩策略由 OpenCode 管理，旧 Agent `ContextPolicy` 不再用于平台摘要调用。

当 Profile 的 Modality 为 `Multimodal` 时，受控大小的 `image/*` 文件会以内联二进制内容发送给模型；文本模型、超出限制的图片或读取失败时只保留 fileId manifest，由文件工具按需读取。默认每张图片最多 4 MiB，每次最多 4 张。

```text
AgentRequest(agentId, llmProfileId, tenantId)
  -> IAgentConfigProvider
  -> ILlmConfigProvider
  -> AgentAuthorizationGate
  -> RuntimeAgentExecutor
  -> OpenCodeRuntime -> ACP session/prompt
  -> OpenCode（模型调用、工具循环与自动压缩）
```

管理 API 对 API Key 只写不读：GET/PUT 响应均返回空 Key；编辑时空 Key 保留旧值；连接测试按租户和 Profile ID 读取已保存的真实 Key。密钥不得进入前端状态、日志或错误响应。

运行时的 ACP `usage_update` 提供上下文占用（Used）与窗口上限（Size）；它不是完整计费用量，缺失字段不伪造。

平台已移除直接模型交互记录器，原交互日志表和查询 API 保留历史读取。ACP 轮次不会通过原记录器保存模型原始请求/响应，详见 [会话文档](../modules/conversation.md)。密钥与附件原始字节不得写入平台日志。
