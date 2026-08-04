# MAF Runtime — 设计说明

身份和模型授权后立即创建 `ChatClientAgent` 与 `AgentSession`。平台不再构造
`ExecutionContext` 或 Engine 请求。

```text
Pipeline -> AgentRun
  -> IdentityResolution
  -> MafAgentFactory -> ChatClientAgent
       -> PlatformChatHistory : ChatHistoryProvider
       -> MafCapabilityProvider : AIContextProvider
       -> CompactionProvider
       -> FunctionInvokingChatClient
  -> Agent.Run[Streaming]Async
```

`PlatformChatHistory` 在 MAF 请求历史时获取分布式锁、加载 Redis/SQL 消息，并在 MAF
结束通知中写回成功、失败或取消状态。`MafCapabilityProvider` 在 MAF 请求上下文时发现
授权能力，直接提供携带执行体的 `AIFunction`。工具名称、描述与 schema 不再复制到
system prompt。

新增 Provider 只扩展 `IMafChatClientFactory`；新增能力只产生 `AIFunction`；新增记忆
实现只扩展 `ChatHistoryProvider`；多 Agent 编排只使用 MAF Workflow。
