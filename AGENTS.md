# AGENTS.md — AI 编码入口

OpenAgent 是基于 .NET 8.0 的多服务 Agent 平台（C#, ASP.NET Core）。

## 模块结构

```
Backend/
├── Agent.Contracts/       共享接口、模型、DTO（纯契约层）
├── Agent.Core/            核心逻辑（管道、中间件、引擎适配、会话锁）
├── Agent.Engine/          Engine 服务（Redis 注册、健康检查、热更新）+ Host（ASP.NET Core）
└── Agent.Hosting/         共享 DI、认证、Redis 与 OpenTelemetry 注册扩展
```

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
| `docs/planning/` | 规划文档与重构基线 |

## 关键约定

- `InternalsVisibleTo` 用于测试访问 — 在假设 `internal` 可见性前先检查 `.csproj`
- TestMCP 使用 SQLite，数据目录通过 `OPENAGENT_TEST_DATA_DIR` 环境变量指定
- Engine Dockerfile：`Backend/Agent.Engine/src/Host/Dockerfile`
- 依赖方向：Contracts ← Core ← Engine ← Host（不可反向）
