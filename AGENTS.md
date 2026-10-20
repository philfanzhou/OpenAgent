# AGENTS.md — AI 编码入口

OpenAgent 是基于 .NET 10 的多服务 Agent 平台（C#, ASP.NET Core）。

## 模块结构与依赖

当前分支在目录重组与独立 ACP SDK 基线上，聊天执行只走 OpenCode ACP，MAF 推理栈（Microsoft.Agents.AI 全系包）已移除。现有 API、Runner 与平台持久化继续复用；Worker、RunGrant、审批仍是独立任务。实现边界见 [OpenCode 运行时](docs/integrations/opencode-runtime.md)。

```text
Backend/src/
├── OpenAgent.Contracts/       共享接口、配置、DTO 与不含 SDK 的能力定义
├── OpenAgent.Application/     原 Core：执行协调、会话、模型授权、工具适配与组合入口
├── OpenAgent.Capabilities/    文件、RAG、Skill 目录、内置工具、调用授权及 Runner 客户端
├── OpenAgent.Engine/          配置、Redis 注册、热重载；Hosting/ 内为合并后的认证与遥测
├── OpenAgent.Engine.Host/     HTTP/SSE、管理端点与最终组合根
├── OpenAgent.Runtime/         ACP/OpenCode 平台驱动、事件与每轮 MCP 桥（不打包）
├── OpenAgent.Runtime.Acp/     独立单包 ACP SDK
├── OpenAgent.Framework/       原 Infrastructure：EF Core、PostgreSQL、Redis 存储
├── OpenAgent.Router/          路由、转发、限流与入口认证
└── OpenAgent.Runner/          现有沙箱与工作区（本次不调整实现）
```

各模块使用同名 `.Tests` 项目；原 Hosting 测试并入 `Engine.Tests/Hosting/`，原 Core 中的文件及独立能力测试迁入 `Capabilities.Tests/`。

| 项目 | 允许引用的平台项目 |
|---|---|
| Contracts、Runtime.Acp | 无 |
| Capabilities、Engine、Framework、Runner | Contracts |
| Application | Contracts、Capabilities |
| Router | Contracts、Engine |
| Runtime | Contracts、Runtime.Acp |
| Engine.Host | Contracts、Application、Capabilities、Engine、Framework、Runtime |

- Engine 不再依赖 Application；共享目录接口归 Contracts。Router 只使用 Engine/Hosting 的共享宿主能力，不引用配置实现或执行代码。
- Capabilities 不引用 MCP 或模型 SDK；MCP server 定义解析与 Skill 包物化在 Application，外部 server 经 ACP 透传给 harness 原生连接。聊天推理由 Contracts 的 IAgentRuntimeDriver 驱动，Application 不引用 Runtime/Runtime.Acp。
- 文件轮次上下文仍按 scoped 注册；发现/调用授权、工具并发、错误信封、Runner 隔离及回收行为保持不变。
- `OpenAgent.Core`、`OpenAgent.Infrastructure`、独立 `OpenAgent.Hosting` 项目已退出解决方案，不新增兼容项目或双注册路径。
- Runtime.Acp 是独立单包 ACP SDK，不引用平台模块，专属公共契约留在同包。每步同步维护 SDK、测试和 [阶段文档](docs/integrations/acp-dotnet-sdk/IMPLEMENTATION.md)。Worker 等空目录仍只代表规划。
- Runtime 不发布 NuGet，仅作为平台适配程序集；SDK 仍只有一个包。没有第二条推理执行路径。
- 项目引用及模块边界由 `Backend/tests/OpenAgent.Architecture.Tests/` 检查；不得为违规实现扩大白名单。
- 文档统一放 `docs/`，源码目录不放 README。

## 开发指南

- 当前待办与分派：[任务清单](docs/planning/TODO.md)；历史方案不作为当前任务状态。
- 模块入口、任务分派、代码审查与验证：[开发指南](docs/overview/DevelopmentGuide.md)。
- Application 按功能域组织；新增能力通过 Tooling 的 ICapabilitySource / IAgentFeatureFactory 接入，Execution 不引用具体能力实现。
- 各模块修改自己的 Extensions 注册文件；Exten/ApplicationServiceExtensions 保留公开组合入口。
- 前端 feature 只依赖本域和 shared，跨域设置由 app 组合；新模块不引用根兼容 facade。
- 后端模块依赖与前端 import 依赖已有测试门禁；不要通过扩大白名单绕过架构问题。
- 所有模块文档仍收入 docs/，源码目录不散放 README。

## 编码规则

权威规则见 `.agent/rules/coding-conventions.md`（机器可检查部分下沉到 `.editorconfig` + `Directory.Build.props`）。

## AI 资源

- `.agent/README.md` — 技能/规则索引
- `.agent/skills/` — 任务工作流（构建、测试、E2E、文档等）
- `.agent/rules/` — 编码规范 + 文档规范

## 文档中心

所有正式文档统一位于 `docs/`：

| 目录 | 内容 |
|------|------|
| `docs/overview/` | 系统上下文、设计、流程、数据所有权 |
| `docs/modules/` | 功能域详细文档（execution、conversation、capabilities、security、engine） |
| `docs/integrations/` | 外部依赖集成（LLM、Redis、PostgreSQL、MCP、RAG） |
| `docs/database/` | 数据存储唯一事实源 |
| `docs/decisions/` | 架构决策（ADR） |
| `docs/planning/TODO.md` | 当前任务、状态、依赖与验收的唯一入口 |
| `docs/archive/` | 历史方案与已整合的旧文档 |
| `docs/test-reports/` | 实测结果与验证记录 |

## 关键约定

- `InternalsVisibleTo` 用于测试访问 — 在假设 `internal` 可见性前先检查 `.csproj`
- 构建/测试：`dotnet build Backend/OpenAgent.sln`、`dotnet test Backend/OpenAgent.sln`
- 包版本统一在 `Backend/Directory.Packages.props` 管理，`.csproj` 中不写 `Version`
- 警告不全局压制；仅对预览/实验性 API（MAAI001/OPENAI001）做必要 NoWarn
