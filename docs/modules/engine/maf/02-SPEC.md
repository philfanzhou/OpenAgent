# MAF Runtime — 规格

- 每个 turn 使用授权后的 `LlmConfig` 创建 `IChatClient` 和 `ChatClientAgent`。
- `UseProvidedChatClientAsIs = true`。
- 每个 run 用 `FunctionInvokingChatClient` 包装 client。
- `MaximumIterationsPerRequest = max(1, AgentConfig.MaxTurns)`。
- 未知函数终止运行；函数连续错误阈值为零。
- 平台权限异常和取消必须原样离开 MAF 循环。
- 工具由携带原始 `ToolDefinition` 的 `AIFunction` 执行。
- 平台 Redis/SQL 会话是唯一持久历史。
- `AddAgentCore` 是唯一 DI 注册入口。

支持的 `ApiFormat`：

- `OpenAIChatCompletions`
- `OpenAIResponses`
- `AnthropicMessages`

消息适配支持 system/user/assistant/tool、function call/result、reasoning、文本附件和
二进制 `DataContent`。
