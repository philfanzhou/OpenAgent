# Agent.Matrix — 配置消费设计

`AgentRun` 在一次 run 前固定配置快照：

```text
AgentRun
  -> IdentityResolution
       -> AgentIdResolver
       -> ExecutionConfigResolver -> IAgentConfigProvider
       -> AgentAuthorizationGate
       -> ILlmRegistry
  -> MafAgentFactory -> MafChatClientFactory -> ChatClientAgent
```

| 配置 | 消费者 |
|---|---|
| `Llm` | `IdentityResolution` 固定授权快照；`MafChatClientFactory` 创建 `IChatClient` |
| `Llm.Temperature` | `MafAgentFactory` 构建 MAF `ChatOptions` |
| `Mcp.Servers` | `ToolAssembler` |
| `Rag` | `ToolAssembler` / `MafCapabilityProvider` |
| `Skills` | `ISkillProvider` |
| `MaxTurns` | `FunctionInvokingChatClient.MaximumIterationsPerRequest` |

`FrameworkType` 只用于旧配置反序列化兼容，不再选择生产引擎。Core 不关心
`IAgentConfigProvider` 的数据来源，可以是 Matrix、Redis、本地配置或测试内存实现。
