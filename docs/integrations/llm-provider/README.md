# LLM Provider

所有生产模型调用通过 `AgentChatClientFactory` 创建 `IChatClient`，支持 OpenAI Chat Completions、OpenAI Responses 与 Anthropic Messages。

## 配置与选择

`LlmProviderProfile` 是租户级独立资源，保存：

- Model ID
- Modality (`Text` 或 `Multimodal`；当前多模态只支持图片)
- Context Tokens (`ContextTokens`，HTTP JSON 为 `contextTokens`)
- API Format
- Endpoint
- Temperature
- API Key（服务端租户绑定加密存储）

Agent 不再绑定 Provider 或 Model。执行请求携带 `llmProfileId`，`AgentRuntimeResolver` 按已验证的 tenantId 分别加载 Agent 与 LLM Profile，完成资源授权后创建本次执行的 `LlmConfig`。模型上下文窗口由 LLM 的 `ContextTokens` 控制，压缩策略仍来自 Agent 的 `ContextPolicy`。

当 Profile 的 Modality 为 `Multimodal` 时，受控大小的 `image/*` 文件会以内联二进制内容发送给模型；文本模型、超出限制的图片或读取失败时只保留 fileId manifest，由文件工具按需读取。默认每张图片最多 4 MiB，每次最多 4 张。

```text
AgentRequest(agentId, llmProfileId, tenantId)
  -> IAgentConfigProvider
  -> ILlmConfigProvider
  -> AgentAuthorizationGate
  -> AgentChatClientFactory
  -> ChatClientAgent
```

管理 API 对 API Key 只写不读：GET/PUT 响应均返回空 Key；编辑时空 Key 保留旧值；连接测试按租户和 Profile ID 读取已保存的真实 Key。密钥不得进入前端状态、日志或错误响应。

Provider 返回的 `UsageDetails` 是 Token 用量唯一权威来源；缺失核心字段时不做本地估算。
