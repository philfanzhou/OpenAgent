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
dotnet test Backend/OpenAgent.Core/OpenAgent.Core.sln
dotnet test Backend/OpenAgent.Engine/OpenAgent.Engine.sln
```

## 文档

- [文档中心](docs/README.md) — 总览、模块、集成、数据库、架构决策
- [.agent/](.agent/README.md) — AI 工具资源（技能、规则、提示词）

## 安全

> ⚠️ **本仓库默认使用 PassThrough 认证处理器，仅适用于开发环境。**
> 部署到生产环境前，请务必阅读 [SECURITY.md](SECURITY.md) 进行安全加固。
