# Design — OpenAgent

## 项目定位

`OpenAgent.Core` 是 MAF-first 的 Agent 核心。Microsoft Agent Framework 已直接编译进
Core，不再存在单独的 MAF 引擎项目或可切换的生产引擎体系。

| 项目 | 说明 |
|---|---|
| `OpenAgent.Contracts` | 共享接口、模型、DTO（纯契约层） |
| `OpenAgent.Core` | 执行入口（AgentExecutor）、MAF runtime、能力、授权、会话 |
| `OpenAgent.Infrastructure` | 持久化（PostgreSQL/S3）、外部资源访问 |
| `OpenAgent.Engine` | Engine 服务（Redis 注册、健康检查、热更新、配置热重载） |
| `OpenAgent.Engine.Host` | ASP.NET Core 宿主（端点、中间件、流式传输） |
| `OpenAgent.Hosting` | 共享 DI、认证、Redis 与 OpenTelemetry 注册扩展 |
| `OpenAgent.Router` | HTTP 入口、会话亲和、限流、转发 |
| `OpenAgent.Architecture.Tests` | 架构依赖与分层约束测试 |
| `OpenAgent.Contracts.Tests` | Contracts 序列化与契约测试 |
| `OpenAgent.Core.Tests` | Core 单元与组件测试 |
| `OpenAgent.Engine.Tests` | Engine 单元与组件测试 |
| `OpenAgent.Hosting.Tests` | Hosting 单元与组件测试 |
| `OpenAgent.Infrastructure.Tests` | Infrastructure 持久化集成测试 |
| `OpenAgent.Router.Tests` | Router 单元与组件测试 |

## 请求主链

```text
HTTP / Channel
  -> Engine.Host middleware: validation / tracing / auth / audit
       -> AgentExecutor
            -> identity + authorized model snapshot
            -> AgentFactory -> ChatClientAgent + AgentSession
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
| Model | `AgentChatClientFactory` | Provider 返回 `IChatClient`，不新增 Engine |
| Capability | RAG `ICapabilitySource`、官方 `McpClientTool`、官方 `AgentSkillsProvider` | MCP/Skill 不复制协议或 Skill 执行运行时；RAG 保留平台授权适配 |
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
services.AddAgentCore(configuration);
services.AddOpenAgentInfrastructure(configuration);
services.AddFileAssetObjectStorage(configuration);
services.AddAgentEngine(configuration);
```

该入口同时注册 MAF model factory、runtime、turn、能力和平台边界。不得在
Core 之外引入第二 Agent composition root。

## 技术栈

- .NET 8
- Microsoft Agent Framework / Microsoft.Extensions.AI
- Model Context Protocol SDK
- PostgreSQL + EF Core conversation and file-asset persistence
- S3-compatible object storage for file bytes
- xUnit
