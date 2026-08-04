# OpenAgent

OpenAgent 是基于 .NET 8 和 ASP.NET Core 的多服务 Agent 平台。生产代码位于 `Backend/`。

## 模块结构

```text
OpenAgent.Engine.Host ──> OpenAgent.Engine ──> OpenAgent.Core ──> OpenAgent.Contracts
                                      └──> OpenAgent.Hosting ──> OpenAgent.Contracts
```

| 模块 | 职责 |
|------|------|
| `OpenAgent.Contracts` | 跨模块接口、配置与 DTO（纯契约层） |
| `OpenAgent.Core` | 执行引擎、会话存储、MCP/RAG/Skill 能力、安全 |
| `OpenAgent.Engine` | Agent 执行服务、注册表、健康检查、配置热重载 |
| `OpenAgent.Engine.Host` | ASP.NET Core 宿主（端点、中间件、流式传输） |
| `OpenAgent.Hosting` | 共享宿主、JWT、Redis 与 OpenTelemetry 注册 |

## 构建与测试

```bash
dotnet build Backend/OpenAgent.sln
dotnet test Backend/OpenAgent.sln
```

## 文档

- [文档中心](docs/README.md) — 总览、模块、集成、数据库、架构决策
- [.agent/](.agent/README.md) — AI 工具资源（技能、规则、指南）

## 安全

> ⚠️ **本仓库默认使用 PassThrough 认证处理器，仅适用于开发环境。**
> 部署到生产环境前，请务必阅读 [安全设计文档](docs/modules/security/README.md) 进行安全加固。
