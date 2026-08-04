# OpenAgent

OpenAgent 是基于 .NET 8 和 ASP.NET Core 的多服务 Agent 平台。生产代码位于 `Backend/`。

## 模块结构

```text
Agent.Engine ──> Agent.Core ──> LLM / MCP / RAG

Agent.Contracts  <- 共享接口与契约
Agent.Hosting    <- 共享宿主、认证与可观测性注册
```

| 模块 | 职责 |
|------|------|
| `Agent.Contracts` | 跨模块接口、配置与 DTO |
| `Agent.Core` | 执行管道、引擎适配、会话、工具与安全 |
| `Agent.Engine` | Agent 执行服务、注册表、健康检查与热更新 |
| `Agent.Hosting` | 共享宿主、JWT、Redis 与 OpenTelemetry 注册 |

## 构建与测试

```bash
dotnet test Backend/Agent.Core/OpenAgent.Core.sln
dotnet test Backend/Agent.Engine/OpenAgent.Engine.sln
```

## 文档

- [Core 文档](Backend/Agent.Core/docs/README.md)
- [Contracts 设计文档](Backend/Agent.Contracts/Agent.Contracts.Design.md)
- [Hosting 设计文档](Backend/Agent.Hosting/docs/Agent.Hosting.Design.md)

## 安全

> ⚠️ **本仓库默认使用 PassThrough 认证处理器，仅适用于开发环境。**
> 部署到生产环境前，请务必阅读 [安全设计文档](Backend/Agent.Core/docs/modules/security/README.md) 进行安全加固。
