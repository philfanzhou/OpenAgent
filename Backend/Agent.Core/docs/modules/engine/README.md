# MAF Runtime

Agent.Core 只有一个生产 Agent runtime：Microsoft Agent Framework。

```text
IAgentPipeline
  -> AgentRun
  -> ChatClientAgent + AgentSession
       -> PlatformChatHistory
       -> MafCapabilityProvider
       -> CompactionProvider
       -> Provider IChatClient
```

Core 不再定义 Engine 请求/响应 Contract，也不存在 `MafEngine`。Provider 差异只在
`IMafChatClientFactory` 中适配；能力、历史和压缩分别使用 MAF 原生扩展点。

生产 Host 只注册：

```csharp
services.AddAgentCore(configuration);
```

- [MAF 功能](./maf/01-FEATURE.md)
- [MAF 设计](./maf/03-DESIGN.md)

`Agent.Workflow` 仍是规划文档，不属于当前运行拓扑。
