# LLM Provider

所有生产模型调用通过 `AgentChatClientFactory` 创建 `IChatClient`，支持 OpenAI Chat Completions、OpenAI Responses 与 Anthropic Messages。

## 配置与选择

`LlmProviderProfile` 是租户级独立资源，保存：

- Model ID
- Context Window Tokens
- API Format
- Endpoint
- Temperature
- API Key（服务端明文存储）

Agent 不再绑定 Provider 或 Model。执行请求携带 `llmProfileId`，`AgentRuntimeResolver` 按已验证的 tenantId 分别加载 Agent 与 LLM Profile，完成资源授权后创建本次执行的 `LlmConfig`。模型上下文窗口覆盖 Agent 旧的 `ContextPolicy.MaxTokens`，其余压缩策略仍来自 Agent。

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
