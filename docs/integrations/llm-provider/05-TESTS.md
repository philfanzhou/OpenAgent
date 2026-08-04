# LLM Provider 集成 — 测试说明

旧 Engine DTO 和委托替身测试已失效。新测试应直接使用 fake `IChatClient` 验证：

- `MafChatClientFactory` 的 Provider 构造；
- `ChatClientAgent` 非流式/流式调用；
- `MafCapabilityProvider` 返回原生 `AITool`；
- `PlatformChatHistory` 的 MAF history 生命周期；
- `CompactionProvider` 策略选择；
- `AgentSession` 内的函数循环、usage 和失败传播。

真实 Provider、Redis、MCP E2E 与本地替身分开报告。
