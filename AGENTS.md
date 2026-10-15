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
│   ├── OpenAgent.Runner/          Runner 沙箱、工作区与资源清理
│   └── OpenAgent.Router/          网关服务（路由、意图识别、限流、租户隔离）
└── tests/
    ├── OpenAgent.Architecture.Tests/
    ├── OpenAgent.Contracts.Tests/
    ├── OpenAgent.Core.Tests/
    ├── OpenAgent.Engine.Tests/
    ├── OpenAgent.Hosting.Tests/
    ├── OpenAgent.Infrastructure.Tests/
    ├── OpenAgent.Router.Tests/
    └── OpenAgent.Runner.Tests/      Runner 沙箱执行测试（BubblewrapFact 环境门控）
```

> 项目名前缀 `OpenAgent.*` 与文件夹前缀对齐。实际引用：Core → Contracts；Engine → Core/Contracts；Infrastructure、Hosting、Runner → Contracts；Router → Hosting/Contracts；Engine.Host 组装下层。不可反向。

## 目标架构与模块规则（Proposal，未批准）

方向见 [新架构提案](docs/architecture/new-architecture.html)，任务见 [任务清单](docs/planning/TODO.md) 的 A 系列。执行路径由 MAF 改为“Control + Worker + 可换 ACP harness”，MAF 路径不保留；沙箱使用 harness 自带机制，`OpenAgent.Runner` 暂不处理。

`Backend/src` 与 `Backend/tests` 下已预建全景目录（仅含 `.gitkeep`）。**目录存在不代表项目已建立**：新增 `.csproj`、加入 `OpenAgent.sln`、引入 ACP SDK 须有对应 A 系列任务；提案批准（A03）前，不从 Core 抽取代码。目录名与提案不一致时先改提案，不另起目录。

通用约束：
- 依赖只能自上而下，Contracts 在最底层；违反规则时改设计，不扩 `Architecture.Tests` 白名单。
- 新增能力不再写进 `OpenAgent.Core`；Core 只减不增，MAF 相关代码不新增功能，只做必要修复。
- 平台决策（准入、状态、租约、审批）在 Application；harness 细节只在 Runtime.Acp；业务能力只在 Capabilities。
- 身份来自 RunGrant，不来自模型参数；写操作必须带 OperationId，不得自动重发 prompt。
- 租约失效的 Worker 必须在写入端被拒（fencing），不能只靠 Redis 锁。
- 文档放 `docs/`，源码目录不放 README。

| 模块 | 状态 | 职责 | 可引用 | 禁止 |
|---|---|---|---|---|
| `Contracts` | 改造 | 跨层数据结构与接口：`Runtime/`（RunSpec、RuntimeBinding、RuntimeEvent、IRuntimeAdapter、`WorkerProtocol/`）、`Grants/`（RunGrant、InvocationContext）、`Audit/`（只写接口）；只有定义没有行为 | 无 | ACP/MCP SDK、EF、DI 实例、任何实现 |
| `Router` | 已有 | 入口认证、路由、会话粘性；`Classification/` 为自带模型与密钥的独立意图分类 | Hosting、Contracts | 执行 Agent、调用 Engine `/intent`、引用 Engine/Core/Application |
| `Engine.Host` | 改造 | Control 进程 HTTP 外壳与组合根：`Endpoints/{ChatCompat,WorkerApi,Approvals}`、`Composition/`（唯一可同时引用多模块处） | Application、Engine、Capabilities.Host、Infrastructure、Hosting | 业务规则、引用 Runtime.Acp/Worker.Host |
| `Application` | 新建 | 平台中枢：`Admission/` 准入与能力匹配、`Runs/` 状态机与取消恢复审批、`Sessions/` 会话与固定绑定、`Leases/` 租约与 fencing、`Events/` 事件序号与入库、`Grants/` 签发门禁卡、`Abstractions/` 存储接口 | Contracts | ACP/harness 类型、直接访问数据库或 Redis、HTTP、Runtime.*、Core、Infrastructure |
| `Engine` | 已有 | Agent 配置存取、Redis 注册、热更新；增加 runtime 选择字段 | Contracts | 执行职责、启动 harness、不新增配置中心 |
| `Runtime.Acp` | 新建 | 用 ACP 驱动 harness：`Protocol/` 握手与会话、`Events/` 通知转 RuntimeEvent、`Recovery/` resume/load 选择、`Compilers/` 每个 harness 一个启动参数编译器、`Manifests/` 允许启动清单；ACP 协议库只在此项目内可见 | Contracts、ACP 协议库 | 数据库、租户、RunGrant 签发、Application、Core、向外暴露 ACP 类型 |
| `Worker.Host` | 新建 | Worker 进程：`Leasing/` 领取续租与失联自停、`Supervision/` 进程树监督与取消、`FileProxy/` 工作文件、`Workspace/` 会话目录、`Snapshot/` 快照；只出站连接 Control | Runtime.Acp、Contracts、Hosting | Infrastructure（业务库与 Redis 凭证）、Application、Capabilities.Host、自建沙箱后端、开放入站端口 |
| `Capabilities` | 新建 | 业务能力本身：`Registry/` 工具白名单、`Invocation/` 超时与结果大小策略、`Files/` `Rag/` `Skills/`（从 Core 迁入）、`Ledger/` OperationId 账本 | Contracts | MCP SDK、AI 框架、HTTP、Runtime.* |
| `Capabilities.Host` | 新建 | harness 的两个窗口（在 Control 进程内）：`Mcp/` 工具注册、`Auth/` RunGrant 验签与租约校验、`Llm/` 模型网关与短期 key、`Audit/` 调用审计；无状态 | Capabilities、Contracts、Hosting、Infrastructure、MCP SDK | 保存“当前用户”状态、Runtime.*、Worker.Host |
| `Infrastructure` | 已有 | 存储实现；新增 `Persistence/{Runs,Bindings,Leases,Events,Ledger}`、`Audit/`、`Queue/`（Redis 仅作调度提示）、`Snapshots/` | Contracts 及被实现接口所在项目 | 被上层以具体类型引用、业务规则 |
| `Hosting` | 已有 | 认证、Redis、OTel 注册；新增 `Telemetry/` traceId 跨 Router→Control→Worker 传播、`Security/` RunGrant 密钥管理 | Contracts | 业务逻辑 |
| `Core` | 过渡 | 迁出期间的过渡层，最终清空移除；MAF 执行路径直接删除 | Contracts | 任何新项目引用它、新增功能 |
| `Runner` | 暂缓 | 现有沙箱 Runner，本方案不处理 | — | 因架构调整而改动 |

测试项目：`Application.Tests`（状态机、租约、幂等）、`Runtime.Acp.Tests`（假 harness 测协议、断线、取消）、`Capabilities.Tests`（授权、伪造主体、路径逃逸）、`Worker.Tests`（双 Worker、失联、进程残留）、`Compatibility.Tests`（真实 harness 认证，按环境开关）。

## 开发指南

- 当前待办与分派：[任务清单](docs/planning/TODO.md)；历史方案不作为当前任务状态。
- 模块入口、任务分派、代码审查与验证：[开发指南](docs/overview/DevelopmentGuide.md)。
- Core 按功能域组织；新增能力通过 Tooling 的 ICapabilitySource / IAgentFeatureFactory 接入，Execution 不引用具体能力实现。
- 各模块修改自己的 Extensions 注册文件；Exten/CoreServiceExtensions 保留公开组合入口。
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
