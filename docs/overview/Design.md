# Design — OpenAgent

## 项目定位

`OpenAgent.Core` 是 MAF-first 的 Agent 核心。Microsoft Agent Framework 已直接编译进
Core，不再存在单独的 MAF 引擎项目或可切换的生产引擎体系。

项目职责、模块目录、扩展接口和测试入口统一见 [开发指南](./DevelopmentGuide.md)。
本页说明运行时架构；持久化主责见 [DataOwnership](./DataOwnership.md)。

## 请求主链

```text
HTTP / Channel
  -> Engine.Host middleware: validation / tracing / auth / audit
       -> AgentExecutor
            -> identity + authorized model snapshot
            -> AgentFactory -> ChatClientAgent + AgentSession
                 -> ChatHistoryProvider -> lock + platform history
                 -> AgentFeature -> tools + AIContextProviders + resources
                 -> CompactionProvider (when enabled)
                 -> FunctionInvokingChatClient
            -> usage + tool marker + SSE mapping
```

MAF 管理 Agent、模型、流式输出、函数回填和工具迭代。平台保留租户授权、会话一致性、
持久化、外部协议和审计。Core 中没有 `IAgentService`、`IAgentEngine`、
`ExecutionInitializer` 或独立 `ConversationExecutor`。

## 并列扩展面

| 扩展面 | 实现 | 约束 |
|---|---|---|
| Model | `AgentChatClientFactory` | Provider 返回 `IChatClient`，不新增 Engine |
| Capability | 普通能力 `ICapabilitySource`；MCP/Skill `IAgentFeatureFactory` 接入官方工具与 Provider | Execution 通过接口组合；能力各自负责 SDK 适配与资源 |
| Memory | `PlatformChatHistory` | 平台存储实现 MAF `ChatHistoryProvider` |
| Orchestration | `ChatClientAgent` / `AgentSession` | 单 Agent 或未来 MAF Workflow 均留在此边界 |

四者不是串行 wrapper。一次请求只创建一个平台 turn，并在其中发起一次 MAF run。
Factory 返回拥有 `AIAgent`、历史和资源生命周期的 `AgentExecutionScope`。轮次身份通过
`TurnContext` 携带，分别投影给会话和交互捕获；转换与观测不反向依赖执行编排。

## 会话一致性

| 层次 | 机制 | 位置 |
|---|---|---|
| 防覆盖 | `expectedVersion` + EF Core 并发令牌 | PostgreSQL conversation store |
| 性能优化 | conversation affinity | Router |

MAF 通过 `PlatformChatHistory` 主动加载和写回历史。PostgreSQL 是会话和文件资产的唯一持久化事实源；S3 兼容对象存储只保存原始文件字节。

## DI 入口

生产 Host 只需：

```csharp
services.AddAgentCore(configuration);
services.AddOpenAgentInfrastructure(configuration);
services.AddFileAssetObjectStorage(configuration);
services.AddAgentEngine(configuration);
```

AddAgentCore 调用各功能域的注册扩展，保留原公共入口；AgentFactory 仅通过下层接口
组合运行时。不得在 Core 之外引入第二 Agent composition root。

## 技术栈

- .NET 8
- Microsoft Agent Framework / Microsoft.Extensions.AI
- Model Context Protocol SDK
- PostgreSQL + EF Core conversation and file-asset persistence
- S3-compatible object storage for file bytes
- xUnit
