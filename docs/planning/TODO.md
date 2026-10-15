# 任务清单

状态核对：2026-10-15，`dev` 基线 `6a3f29f`。本页是当前待办的唯一入口；方向以[新架构提案](../architecture/new-architecture.html)（Proposal，未批准）为准，模块实际目录见 [开发指南](../overview/DevelopmentGuide.md)，旧方案见 [历史归档](../archive/README.md)。

## 状态与分派规则

- `[x]` 表示已合并并有验收记录；`[ ]` 表示仍有工作。下列未完成任务默认状态为**待开发**、负责人为**未分配**，不能用已有 PR 代替分派或验收。
- 领取任务时在该项补充“负责人 / 状态（待开发、开发中、待评审、待验证、阻塞） / 实现 PR / 验收证据”；有阻塞需记录原因与依赖 ID。
- 每项独立 PR；写明拥有目录、共享文件、契约、验证和回退。公共 DTO、Host Program、项目/包引用、数据库模型/迁移及前端 app 由集成人协调。同一文件的迁移先合入共同基线。
- 后端范围以下以 `Backend/src/OpenAgent.*` 为根，前端以 `Frontend/OpenAgent.Chat/src/` 为根。结构迁移保持协议和行为兼容；功能/schema 变化单独验收及制定回退方案。

## 已完成基线

- [x] **B01 Core 第一批边界**：功能归域、模块 DI、MCP/Skill 扩展接口、Runner 客户端及产物发布复用、测试目录与依赖门禁。证据：[PR #158](https://github.com/philfanzhou/OpenAgent/pull/158)、[验证记录](../test-reports/2026-10-08-module-boundaries.md)。
- [x] **B02 前端第一批边界**：shared transport/wire/SSE、业务 API 归域、模型/MCP/Skill/RAG 设置模块与 import 门禁。证据同 B01。第一批边界不代表下列功能已完成。

## 方向变更

执行路径由 MAF 改为“平台控制面 + Worker + 可换 ACP harness”，MAF 执行路径不保留，沙箱使用 harness 自带机制（如 Codex CLI），`OpenAgent.Runner` 暂不处理。旧清单中依赖 MAF 的任务已取代或并入下列 A 系列；任务按 A 系列重排，批准提案前不抽取 Core。

## P0：验证与准备

- [ ] **A01 .NET 10 升级**（待分派，须在 2026-11-10 .NET 8 停止支持前完成）。主责：`global.json`、`Directory.Build.props`、`Directory.Packages.props`；与功能改动分开提交。
  验收：全解决方案构建与测试通过；MCP SDK、EF Core、Microsoft.Agents.AI 在 .NET 10 下逐一确认；不支持的依赖单独记录结论，不阻塞其余升级。
- [ ] **A02 ACP 端到端 spike**（待分派）。主责：新建临时 spike 项目，不改现有 Core/Engine；以 OpenCode 为首个 harness。
  验收：走通领任务、启动 harness、事件回传、经 MCP 调一个 C# 工具、取消；验证 ACP 协议库的会话恢复、反向权限、断线取消；实测单会话冷启动耗时与单机并发上限；记录 harness 自带沙箱的隔离反例（同名会话、越界路径、HOME 泄漏）。可从 `origin/codex/runtime-adapters-acp`（PR #229）逐块移植协议帧、失败分类与事件映射，不整体合并。
- [ ] **A03 提案评审与 ADR 拆分**（待分派，依赖 A02 结论）。主责：`docs/architecture`、`docs/decisions`。
  验收：提案补齐 MAF 移除顺序与前端兼容层退出条件后批准；第 8 章决策拆成 ADR，同时修正两个 `0004` 重复编号；批准后更新 overview 与模块文档，批准前不改写它们。

## P1：新架构落地（依赖 A03）

- [ ] **A04 Runtime 契约与 Worker**（待分派）。主责：Contracts/Runtime、新建 Runtime.Acp、Worker.Host；纯新增，先于任何 Core 抽取。
  验收：IRuntimeAdapter、RuntimeEvent、RunSpec、Worker 协议契约；假 harness 测试覆盖协议、断线、取消；Worker 租约失联后自停。
- [ ] **A05 MCP 网关与 RunGrant**（待分派，吸收原 F03）。主责：新建 Capabilities（首版含网关，不拆 Host 项目）；交界：Hosting、Engine.Host。
  验收：RunGrant 签发与验签、调用主体不依赖模型参数、跨租户凭证不复用、凭证可轮换与撤销、认证失败可审计；Core 中的文件/RAG/Skill/工具业务以 MCP 工具形式暴露。
- [ ] **A06 Application 与事件持久化**（待分派，吸收原 F01 的记录与重放目标）。主责：新建 Application；从 Core/Conversation、Security 迁出。
  验收：准入与能力匹配、Run/Session 状态机、会话租约与 fencing、事件按粒度入库并可重放、异常带完整调用阶段与 Tenant/User/Conversation/Trace/Agent 关联、凭证脱敏。
- [ ] **A07 旧聊天接口兼容**（待分派）。主责：Engine.Host/Endpoints/ChatCompat。
  验收：RuntimeEvent 映射为现有前端 SSE；契约测试锁定当前事件形态；取消、刷新重建、会话列表行为不变。
- [ ] **A08 审批**（待分派，取代原 F07）。主责：Application、Engine.Host/Endpoints/Approvals；交界：前端 chat。
  验收：ACP 权限请求映射为待审批状态；批准、拒绝、超时、取消、重启续接都有明确状态；批准前不执行风险操作。

## P2：切换与移除

- [ ] **A09 切流并移除 MAF 路径**（待分派，依赖 A04–A08 验收及真实 harness 验证）。主责：Core/Execution、ModelProviders、MAF 适配及 Microsoft.Agents.AI 包引用。
  验收：全部请求走 harness 路径且回归通过；删除 MAF 代码与包，Core 清空；Architecture.Tests 增加新依赖规则；`OpenAgent.Runner` 不受影响。

## 保留的独立任务

- [ ] **F04 MCP/Skill/Agent 统一授权**（待分派）。主责：Contracts、Security；与 A05 共定主体、资源、动作及租户范围。
  验收：能力发现和实际调用均检查权限；覆盖越权、跨租户、权限撤销与默认策略；目录可见性与执行权限一致，审计可追溯。参考 [授权文档](../modules/security/permission/README.md)。
- [ ] **F05 生产容灾备份**（待分派，生产上线前必须验收）。主责：数据库与部署运维；交界：Infrastructure、对象存储、配置密钥。
  验收：确定 RPO/RTO、保留周期和恢复责任人；自动备份 PostgreSQL，并明确 S3 文件与 Data Protection 密钥的配套恢复；失败告警、恢复脚本和实际恢复演练记录。
- [ ] **F06 Workflow 多 Agent 编排**（暂缓，待 A03 后按新架构重新评估）。不得将现有 `workflow` 路由占位当成已部署服务。关联 [Issue #150](https://github.com/philfanzhou/OpenAgent/issues/150)。

## 与 MAF 无关的结构拆分

以下任务不涉及要移除的执行路径，可独立推进；目标目录不表示已经存在。

- [ ] **S02 / T11** — Host/Extensions 端点归 Endpoints 对应域与 Admin/{Agents,Models,Mcp,Rag,Skills}；流式 writer/headers/heartbeat/payload 归 Streaming
  依赖与验收：协调 I03；保留 Map* 汇总、认证、Development-only 管理映射及 HTTP/SSE 顺序与 done
- [ ] **S03 / T12** — Host/Skills/SkillPackageManagementService 归 Engine/Management/Skills；Contracts 提供纯命令/结果，Host 只映射 HTTP
  依赖与验收：依赖 S02；Engine internal 实现；验证上传更新删除、绑定、版本冲突与失败清理
- [ ] **S04 / T13** — Host/Files 的 S3FileObjectStore、S3Presigner、存储 Options/Validator 归 Infrastructure/ObjectStorage；健康检查归 Host/Health
  依赖与验收：可独立推进；AWSSDK.S3 同迁、Host 注册门面保留且不双注册；上传下载、签名、校验与 readiness 一致
- [ ] **S05 / T14** — Infrastructure/Persistence/OpenAgentDbContext.OnModelCreating 拆为 Mapping 下每实体 IEntityTypeConfiguration
  依赖与验收：可独立推进；设计时模型与快照一致，关系/index/schema 不变，不生成 schema migration
- [ ] **S06 / T15** — 核对 shared wire 类型生成与 UI 类型所有权
  依赖与验收：先查实际主干，旧方案的 #121 不代表 codegen 已落地；保留 JSON 与兼容导出；若需生成，验收生成一致性与 CI 漂移检查
- [ ] **S07 / T16** — app Agent 编辑按独立开发需要迁入 features/agents，app 保留组合
  依赖与验收：保留 CRUD、草稿、Agent 选择与切页状态；与 S06 协调类型
- [ ] **S08 / T17** — useChatStreaming/useConversationStreams、ChatMessages/MessageComposer 归 chat；会话状态、conversationCollection、ChatSidebar 归 conversations
  依赖与验收：app 注入会话选择；验证串流、取消、刷新重建与列表状态
- [ ] **S09 / T17** — useAuthentication/auth/LoginPage 归 auth；useFileHandling/markdownAssets 归 files；健康检查、交互调试与追溯 UI 归 diagnostics
  依赖与验收：shared 不反向依赖 auth；Markdown 注入文件解析器；验证登录、上传授权、健康检查与追溯
- [ ] **S10 / T17** — 通用 markdown/typewriterQueue/streamingAssistantContent 归 shared；usePanelLayout 归 app
  依赖与验收：与 S08/S09 协调同文件迁移；shared 无 feature 业务状态，app 只组合工作台

## 已取代或暂缓

| 原任务 | 处置 |
|---|---|
| F01 完整异常记录与重放 | 并入 A06 |
| F02 超长上下文与压缩恢复 | 取代：上下文压缩由 harness 承担 |
| F03 MCP 可靠认证 | 并入 A05 |
| F07 风险操作审批 | 取代为 A08 |
| S01 PlatformChatHistory 拆分 | 取代：属于 MAF 历史路径，随 A09 删除 |
| S11 Runner 拆分 | 暂缓：沙箱暂不处理 |
| S12 剩余域收口 | 取代：验收并入 A09 |
| I01–I06（PR #144、#146、#152–#156） | 取代：均为 MAF 路径；不再合并，处置方式待确认 |
| I07（PR #157 RAG Provider 验证） | 取代：RAG 以 MCP 工具形式落在 A05；PR 处置待确认 |

## 建议执行顺序

1. A01 与 A02 并行；A03 在 A02 结论后评审。
2. A04、A05 纯新增先行，再做 A06、A07；A08 随 A06 之后。
3. A09 最后，F04、F05 与上述并行推进；前端与 Host 拆分按拥有目录独立进行。
