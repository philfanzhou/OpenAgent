# AGENTS.md — AI 编码入口

OpenAgent 是基于 .NET 8.0 的多服务 Agent 平台（C#, ASP.NET Core）。

## 模块结构

```
Backend/
├── OpenAgent.sln                  统一解决方案
├── Directory.Build.props          编译约束（TFM、Nullable、ImplicitUsings）
├── Directory.Packages.props       集中包版本管理（Central Package Management）
├── src/
│   ├── OpenAgent.Contracts/       共享接口、模型、DTO（纯契约层）
│   ├── OpenAgent.Core/            核心逻辑（执行引擎、能力、会话抽象、安全）
│   ├── OpenAgent.Engine/          Engine 服务（Redis 注册、健康检查、热更新、配置热重载）
│   ├── OpenAgent.Engine.Host/     ASP.NET Core 宿主（端点、中间件、流式传输）
│   ├── OpenAgent.Hosting/         共享 DI、认证、Redis 与 OpenTelemetry 注册扩展
│   ├── OpenAgent.Infrastructure/  持久化实现（PostgreSQL+EF Core、Redis 写穿缓存、分布式锁）
│   └── OpenAgent.Router/          网关服务（路由、意图识别、限流、租户隔离）
└── tests/
    ├── OpenAgent.Architecture.Tests/
    ├── OpenAgent.Contracts.Tests/
    ├── OpenAgent.Core.Tests/
    ├── OpenAgent.Engine.Tests/
    ├── OpenAgent.Hosting.Tests/
    ├── OpenAgent.Infrastructure.Tests/
    └── OpenAgent.Router.Tests/
```

> 项目名前缀 `OpenAgent.*` 与文件夹前缀对齐。依赖方向：Contracts ← Core ← {Engine, Infrastructure, Router} ← {Engine.Host, Hosting}（不可反向）。

## 编码规则

权威规则见 `.agent/rules/coding-conventions.md`（机器可检查部分下沉到 `.editorconfig` + `Directory.Build.props`）。

## AI 资源

- `.agent/README.md` — 技能/规则索引
- `.agent/skills/` — 任务工作流（构建、测试、E2E、文档等）
- `.agent/rules/` — 编码规范 + 文档规范 + 开发指南

## 文档中心

所有正式文档统一位于 `docs/`：

| 目录 | 内容 |
|------|------|
| `docs/overview/` | 系统上下文、设计、流程、数据所有权 |
| `docs/modules/` | 功能域详细文档（execution、conversation、capabilities、security、engine） |
| `docs/integrations/` | 外部依赖集成（LLM、Redis、SQL、MCP、RAG） |
| `docs/database/` | 数据存储唯一事实源 |
| `docs/decisions/` | 架构决策归档（ADR） |

## 关键约定

- `InternalsVisibleTo` 用于测试访问 — 在假设 `internal` 可见性前先检查 `.csproj`
- 构建/测试：`dotnet build Backend/OpenAgent.sln`、`dotnet test Backend/OpenAgent.sln`
- 包版本统一在 `Backend/Directory.Packages.props` 管理，`.csproj` 中不写 `Version`
- 警告不全局压制；仅对预览/实验性 API（MAAI001/OPENAI001）做必要 NoWarn
