# OpenAgent Backend

OpenAgent 后端仓库：基于 .NET 8.0 的多服务 Agent 平台（C#, ASP.NET Core）。本仓库由原 OpenAgent 单体仓库拆分而来，包含全部后端服务、测试、文档、部署配置，以及原仓库根目录的工程配置（`AGENTS.md`、`.agent/`、`.github/`、`scripts/`，由本仓库负责维护）。前端工作台见 OpenAgent-Frontend 仓库（Vue 3 + TypeScript + Vite，原 `Frontend/OpenAgent.Chat` 原样拆出）。

> 原 monorepo 根 README 已原样归档至 [docs/archive/monorepo-root-README.md](docs/archive/monorepo-root-README.md)。

## 模块结构

```
├── OpenAgent.sln                  统一解决方案
├── Directory.Build.props          编译约束（TFM、Nullable、ImplicitUsings）
├── Directory.Packages.props       集中包版本管理（Central Package Management）
├── global.json                    .NET SDK 版本锁定
├── AGENTS.md                      AI 编码入口（原 monorepo 根配置）
├── .agent/                        技能与规则索引（原 monorepo 根配置）
├── .github/                       CI 与仓库配置（原 monorepo 根配置）
├── scripts/                       构建/镜像脚本（原 monorepo 根配置）
├── src/
│   ├── OpenAgent.Contracts/       共享接口、模型、DTO（纯契约层）
│   ├── OpenAgent.Core/            核心逻辑（执行引擎、能力、会话抽象、安全）
│   ├── OpenAgent.Engine/          Engine 服务（Redis 注册、健康检查、热更新、配置热重载）
│   ├── OpenAgent.Engine.Host/     ASP.NET Core 宿主（端点、中间件、流式传输）
│   ├── OpenAgent.Hosting/         共享 DI、认证、Redis 与 OpenTelemetry 注册扩展
│   ├── OpenAgent.Infrastructure/  持久化实现（PostgreSQL+EF Core、Redis 写穿缓存、分布式锁）
│   ├── OpenAgent.Runner/          Runner 沙箱、工作区与资源清理
│   └── OpenAgent.Router/          网关服务（路由、意图识别、限流、租户隔离）
├── tests/                         各模块测试（Architecture / Contracts / Core / Engine / Hosting / Infrastructure / Router / Runner）
├── docs/                          文档中心（原仓库 docs/ 全量保留）
└── deploy/                        部署配置（基础设施与服务 docker-compose、Keycloak realm、Nginx 模板）
```

> 项目名前缀 `OpenAgent.*` 与文件夹前缀对齐。依赖方向：Core → Contracts；Engine → Core/Contracts；Infrastructure、Hosting、Runner → Contracts；Router → Hosting/Contracts；Engine.Host 组装下层。不可反向。

## 构建与测试

```bash
dotnet build OpenAgent.sln
dotnet test OpenAgent.sln
```

- 包版本统一在 `Directory.Packages.props` 管理，`.csproj` 中不写 `Version`。
- `InternalsVisibleTo` 用于测试访问 — 在假设 `internal` 可见性前先检查 `.csproj`。
- 警告不全局压制；仅对预览/实验性 API（MAAI001/OPENAI001）做必要 NoWarn。
- Runner 沙箱执行测试为 `BubblewrapFact` 环境门控（Linux Bubblewrap）。

> 注：`scripts/` 与 `AGENTS.md` 中的路径描述来自原 monorepo（如 `Backend/OpenAgent.sln`、`Frontend/OpenAgent.Chat`），在本仓库中对应根目录的 `OpenAgent.sln`，前端见 OpenAgent-Frontend 仓库。

## 与前端的边界

- 前端通过 Router（生产入口）或直连 Engine（仅开发联调）访问后端 REST/SSE 接口。
- 接口契约与安全边界见 [`docs/modules/`](docs/modules/README.md) 与 [`docs/overview/`](docs/overview/)。
- 前端仓库：OpenAgent-Frontend。

## 文档中心

所有正式文档统一位于 `docs/`（自原仓库原样保留）：

| 目录 | 内容 |
|------|------|
| `docs/overview/` | 系统上下文、设计、流程、数据所有权 |
| `docs/modules/` | 功能域详细文档（execution、conversation、capabilities、security、engine、chat-workspace） |
| `docs/integrations/` | 外部依赖集成（LLM、Redis、PostgreSQL、MCP、RAG） |
| `docs/database/` | 数据存储唯一事实源 |
| `docs/decisions/` | 架构决策（ADR） |
| `docs/planning/TODO.md` | 当前任务、状态、依赖与验收的唯一入口 |
| `docs/archive/` | 历史方案与已整合的旧文档（含拆分前的 monorepo 根 README） |
| `docs/test-reports/` | 实测结果与验证记录 |

## 部署

入口见 [`deploy/README.md`](deploy/README.md)：基础设施 compose（PostgreSQL、Redis、MinIO、Keycloak）、服务 compose（Engine、Runner、Router、Chat、Nginx）与环境变量模板。

## License

见 [LICENSE](LICENSE)。
