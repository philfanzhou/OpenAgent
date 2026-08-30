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
| `docs/integrations/` | 外部依赖集成（LLM、Redis、PostgreSQL、MCP、RAG） |
| `docs/database/` | 数据存储唯一事实源 |
| `docs/decisions/` | 架构决策归档（ADR） |

## 关键约定

- `InternalsVisibleTo` 用于测试访问 — 在假设 `internal` 可见性前先检查 `.csproj`
- 构建/测试：`dotnet build Backend/OpenAgent.sln`、`dotnet test Backend/OpenAgent.sln`
- 包版本统一在 `Backend/Directory.Packages.props` 管理，`.csproj` 中不写 `Version`
- 警告不全局压制；仅对预览/实验性 API（MAAI001/OPENAI001）做必要 NoWarn

## 维护方式

- 本文件是 AI 协作流程、review 政策和项目边界的统一入口；Codex 直接读取，Claude Code 通过根目录 `CLAUDE.md` 导入。
- `.agent/rules/` 和 `.agent/skills/` 保留编码细则与任务工作流；它们可以补充本文件，但不得覆盖范围、安全、依赖方向和验证约束。
- 本仓库没有单独的 `CONTRIBUTING.md`；贡献者和 AI 使用同一套 issue/PR 流程。

## 文档与沟通语言

- 流程与约束文档、GitHub issue/PR 正文和 review 全程使用中文；Issue 标题使用中文。
- PR 标题使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` 等）。
- 面向使用者的 README、docs、UI/API 文案按现有产品语言保持中文；代码标识符和 commit message 保持英文。
- 引用代码、命令、路径、配置键和诊断码时保持原样。

## 项目边界与契约

- 依赖方向固定为 `Contracts ← Core ← {Engine, Infrastructure, Router} ← {Engine.Host, Hosting}`，不得反向引用或让 Host 细节进入 Core/Contracts。
- `Backend/Directory.Packages.props` 是包版本唯一来源；新增依赖前先确认没有现有项目或平台能力可复用。
- 当前后端目标框架是 .NET 8；不要仅为“升级到最新”而改变 TFM。前端使用 `Frontend/OpenAgent.Chat` 的 pnpm 锁定依赖。
- Redis key、租户隔离、Agent ACL、能力权限、认证边界、PostgreSQL/Redis/MinIO 持久化和 Docker Compose 网络均属于契约，改变前必须写入 issue 的范围与验收标准。
- Development Basic 兼容登录只用于本地联调，不得扩大到生产认证或公网部署。

## 范围纪律

一个 PR 只关闭一个可实施的 task issue。开始前完整阅读 issue 指向的实现、测试、docs、配置、Compose 和部署路径；发现的既有缺陷是邻近债务，
必须单独开 issue 并在目标 issue 的“已知邻近问题”中链接。

一个 issue 只有在以下条件都满足时才可标记 `status: ready`：

1. 写清 `## 范围`（含明确排除项）和可逐条验证的 `## 验收标准`。
2. 安全或健壮性任务写清保证、不保证和调用方责任；不适用时明确写“无”。
3. 将要改动的实现、测试和契约已经完整读过。
4. 邻近债务已经各自开成 issue 并完成链接；确认没有时明确写“无”。
5. 前置 issue 已关闭，GitHub 原生依赖关系与标签一致。

实施中以 issue 范围为约束：不改变明确排除的行为，不顺手重构、改名、升级依赖或修复相邻缺陷；先找不变量，再在路径汇合处修复并覆盖相关输入集合。
成功路径以及适用的失败、取消、安全、认证和并发行为必须与实现一起测试。若验收标准确实要求越过原范围，先更新或拆分 issue。

Review 意见先分类：本 PR 新增/实质修改的缺陷在本 PR 修复；既有或仅与 diff 相邻的问题单独开 issue 并链接，不能顺手修复。只有既有缺陷导致本 PR
某条验收标准无法验证时，才可作为越界例外，并明确指出该条标准。PR 进入第三轮 review 时暂停写代码，逐 commit 审计范围；不能追溯到验收标准的改动移出并改为 follow-up issue。

## 安全与变更纪律

- 不得提交模型 API key、OIDC secret、数据库连接字符串、refresh token、用户数据、Redis/MinIO 凭据或包含它们的日志、截图和测试输出。
- 新增外部集成、网络端点、文件资产或租户能力时，必须补充失败、取消、权限和资源边界测试。
- 只读分析不得修改文件；保留用户已有且与任务无关的改动，不回退、覆盖、提交或推送它们。
- 提交前检查文档链接、模板格式、secret 和仓库状态。

## 验证

按改动风险运行最小充分验证，并在 PR 中记录实际命令、结果和跳过原因：

```bash
dotnet restore Backend/OpenAgent.sln
dotnet build Backend/OpenAgent.sln --configuration Release --no-restore
dotnet test Backend/OpenAgent.sln --configuration Release --no-build --no-restore

cd Frontend/OpenAgent.Chat
corepack pnpm install --frozen-lockfile
corepack pnpm type-check
corepack pnpm test
corepack pnpm build
```

触及 Compose、数据库、Redis、MinIO、Keycloak 或跨服务协议时，按 `.agent/skills/e2e-test.md` 启动对应栈并记录健康检查、日志和清理结果。

## 合并 PR 后

1. 确认远端 PR 已合并，目标分支包含结果；检查并按完成程度处理唯一关联 issue，无关联时明确说明。
2. 确认远端工作分支已删除；保留时说明原因。
3. 用 `git worktree list` 检查并安全清理不再需要的 worktree，然后运行 `git worktree prune`。
4. 用 `git switch main && git merge --ff-only origin/main` 更新目标分支；若被其他 worktree 占用，先处理占用者并说明。
5. 通过 PR 状态或实质 diff 确认改动已进入目标分支后，再清理本地工作分支和远端跟踪引用；不要把 `git branch -d` 失败当作未合并证据。
6. 汇报 PR、issue、远端/本地分支、worktree 和验证结果；未完成的清理必须说明原因与后续动作。
