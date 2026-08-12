# Design — Agent.Core

## 项目定位

`OpenAgent.Core` 是 MAF-first 的 Agent 核心。Microsoft Agent Framework 已直接编译进
Core，不再存在单独的 MAF 引擎项目或可切换的生产引擎体系。

| 项目 | 说明 |
|---|---|
| `OpenAgent.Core` | Pipeline、MAF runtime、能力、授权、会话与持久化 |
| `OpenAgent.Core.Tests` | Core 单元与组件测试 |

## 请求主链

```text
HTTP / Channel
  -> IAgentPipeline
       -> middleware: validation / tracing / auth / audit
       -> AgentRun
            -> identity + authorized model snapshot
            -> MafAgentFactory -> ChatClientAgent + AgentSession
                 -> ChatHistoryProvider -> lock + platform history
                 -> AIContextProvider -> authorized AIFunction
                 -> CompactionProvider
                 -> FunctionInvokingChatClient
            -> usage + tool marker + SSE mapping
```

MAF 管理 Agent、模型、流式输出、函数回填和工具迭代。平台保留租户授权、会话一致性、
持久化、外部协议和审计。Core 中没有 `IAgentService`、`IAgentEngine`、
`ExecutionInitializer` 或独立 `ConversationExecutor`。

## 并列扩展面

| 扩展面 | 实现 | 约束 |
|---|---|---|
| Model | `IMafChatClientFactory` | Provider 返回 `IChatClient`，不新增 Engine |
| Capability | `MafCapabilityProvider` | MCP/Skill/RAG 以受控 `AIFunction` 暴露 |
| Memory | `PlatformChatHistory` | 平台存储实现 MAF `ChatHistoryProvider` |
| Orchestration | `ChatClientAgent` / `AgentSession` | 单 Agent 或未来 MAF Workflow 均留在此边界 |

四者不是串行 wrapper。一次请求只创建一个平台 turn，并在其中发起一次 MAF run。
Factory 直接返回 `AIAgent`；不存在额外 binding/context 聚合对象。可观测性停留在
middleware scope、请求审计和失败边界，不作为参数进入 History 或 Capability。

## 会话一致性

| 层次 | 机制 | 位置 |
|---|---|---|
| 防覆盖 | `expectedVersion` + EF Core 并发令牌 | PostgreSQL conversation store |
| 性能优化 | conversation affinity | Router |

MAF 通过 `PlatformChatHistory` 主动加载和写回历史。PostgreSQL 是会话和文件资产的唯一持久化事实源；S3 兼容对象存储只保存原始文件字节。

## DI 入口

生产 Host 只需：

```csharp
services.AddOpenAgentPostgres(configuration);
services.AddAgentCore(configuration);
```

该入口同时注册 MAF model factory、runtime、turn、能力和平台边界。不得恢复
`AddMafEngine` 之类的第二 composition root。

## 技术栈

- .NET 8
- Microsoft Agent Framework / Microsoft.Extensions.AI
- Model Context Protocol SDK
- PostgreSQL + EF Core conversation and file-asset persistence
- S3-compatible object storage for file bytes
- xUnit
