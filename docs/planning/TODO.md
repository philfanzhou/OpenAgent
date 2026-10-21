# 任务清单

状态核对：2026-10-21；SDK PR #233 与 OpenCode 运行时 PR #234 均已合并到 `dev`。本页是当前待办的唯一入口；历史功能方向以[新架构提案](../architecture/new-architecture.html)（Proposal，未批准）为准，模块实际目录见 [开发指南](../overview/DevelopmentGuide.md)，旧方案见 [历史归档](../archive/README.md)。

## 状态与分派规则

- `[x]` 表示已合并并有验收记录；`[ ]` 表示仍有工作。下列未完成任务默认状态为**待开发**、负责人为**未分配**，不能用已有 PR 代替分派或验收。
- 领取任务时在该项补充“负责人 / 状态（待开发、开发中、待评审、待验证、阻塞） / 实现 PR / 验收证据”；有阻塞需记录原因与依赖 ID。
- 每项独立 PR；写明拥有目录、共享文件、契约、验证和回退。公共 DTO、Host Program、项目/包引用、数据库模型/迁移及前端 app 由集成人协调。同一文件的迁移先合入共同基线。
- 后端范围以下以 `Backend/src/OpenAgent.*` 为根，前端以 `Frontend/OpenAgent.Chat/src/` 为根。结构迁移保持协议和行为兼容；功能/schema 变化单独验收及制定回退方案。

## 已完成基线

- [x] **B01 Core 第一批边界**：功能归域、模块 DI、MCP/Skill 扩展接口、Runner 客户端及产物发布复用、测试目录与依赖门禁。证据：[PR #158](https://github.com/philfanzhou/OpenAgent/pull/158)、[验证记录](../test-reports/2026-10-08-module-boundaries.md)。
- [x] **B02 前端第一批边界**：shared transport/wire/SSE、业务 API 归域、模型/MCP/Skill/RAG 设置模块与 import 门禁。证据同 B01。第一批边界不代表下列功能已完成。

## 当前结构整理（独立于 ACP 功能迁移）

用户已批准按 [新架构方案的目录图](../architecture/新架构方案.html) 重组源码并保留现有行为：Core → Application，Infrastructure → Framework，Hosting 合并 Engine；独立能力迁入 Capabilities，MAF/MCP 适配继续由 Application 执行。不新增 ACP/Worker/审批/RunGrant 功能，不改数据库 schema。

- **R01 目录与边界重组**：实现与本地验证完成；交付分支为 `dev`，尚未合并至 `main`，远端 CI 与部署未验收；覆盖源码、namespace、项目引用、架构门禁、测试归属与 CI/规则路径。迁移前：917 通过、24 跳过；迁移后：919 通过、24 跳过、0 失败，完整构建 0 警告、0 错误。证据：[验收记录](../test-reports/2026-10-15-directory-restructure.md)。
- 下列 A 系列仍是尚未批准的功能规划，不作为本次目录迁移的前置门禁。下文主责路径已按当前模块更新；2026-10-19 用户另行授权本轮 OpenCode ACP 聊天执行切换；Worker/RunGrant 和彻底删除 MAF 包仍按 A 系列单独验收。

## 方向变更

执行路径由 MAF 改为“平台控制面 + Worker + 可换 ACP harness”，MAF 执行路径不保留，沙箱使用 harness 自带机制（如 Codex CLI），`OpenAgent.Runner` 暂不处理。旧清单中依赖 MAF 的任务已取代或并入下列 A 系列；后续功能任务按 A 系列评审；本次已授权的行为保持式结构整理不受 A03 限制。

## 独立 ACP SDK（单包）

本次用户授权 P0–P7 集中实现并创建新 PR；不启动平台切流、Worker 或生产发布。负责人：Codex；状态：已合并；实现 PR：[#233](https://github.com/philfanzhou/OpenAgent/pull/233)。源码范围为 Runtime.Acp 及对应 Tests；SDK 专属契约留同包，不依赖平台模块。测试控制台、官方 SDK 互通夹具、AOT/包检查脚本与 CI 模板按用户要求移出本 PR。

| 任务 | 状态与验收入口 |
|---|---|
| ACP-00 / P0 工程与传输 | 已合并；framing、所有权、依赖门禁 |
| ACP-01 / P1 模型与序列化 | 已合并；按本次精简要求改为手写 DTO + STJ 元数据，schema 只作固定契约摘要 |
| ACP-02 / P2 双向 RPC | 已合并；重入、batch、一般取消、配额、关闭、诊断 |
| ACP-03 / P3 Client/Agent | 已合并；会话、流式、早到更新、取消确认 |
| ACP-04 / P4 本地进程 | 已合并；stdio、环境策略、stderr、退出与租约 |
| ACP-05 / P5 会话与配置 | 已合并；load/resume/list/close/delete、modes/config、14 更新 |
| ACP-06 / P6 交互 | 已合并；权限、文件、终端、elicitation、认证 |
| ACP-07 / P7 验证与发布准备 | 已合并；压力与分配回归、单程序集包边界由单测与依赖门禁覆盖；互通/AOT/CI 脚手架已按用户要求移出，恢复时另行实现并实测；真实模型/稳定版发布分别验收 |

阶段定义见 [IMPLEMENTATION](../integrations/acp-dotnet-sdk/IMPLEMENTATION.md)。PR #231/#232 保持原状，本次提供独立替代实现，不将未合并 PR 标成已完成。

## OpenCode ACP 聊天运行时（依托 #233）

用户已指定 OpenCode，并授权在第二个 PR 切换聊天执行入口。负责人：Codex；状态：已合并；实现 PR：[#234](https://github.com/philfanzhou/OpenAgent/pull/234)（已改基到 `dev`）；本地证据见 [接入记录](../test-reports/2026-10-19-opencode-runtime.md) 与 [独立 Runtime 与 PostgreSQL 验收](../test-reports/2026-10-21-runtime-postgres.md)。

- [ ] **ACP-R01 平台契约与默认执行入口**：Contracts/Runtime + Application/Execution；平台保留授权、历史和持久化，不引用 ACP 公共类型。
- [ ] **ACP-R02 OpenCode 驱动与工具桥**：Runtime 类库（不打包）；固定版本 ACP 进程、隔离环境、每轮 loopback MCP bearer、工具预算及事件合并。
- [ ] **ACP-R03 验证与交付**：真实进程/本地模型/平台 C# 工具、取消、历史续聊、HTTP/SSE；补齐配置、镜像与文档；已移除 MAF 回归快照，补充免 Docker 本机组合、前端默认模型选择、真实 Skill/MCP 原生调用与真实 Anthropic 兼容模型验收。
- [ ] **ACP-R04 运行时延迟优化**（本地实现与验证完成，待 PR 评审）：① `OpenCodeProcessKeeper` 按"会话归属 + 启动身份"复用空闲进程（实测追问轮 -63%，但 OpenCode 1.19.3 空闲进程 1–2 核空转 + 持续外呼，故 `KeepAliveSeconds` 默认 0，机制保留待上游空闲行为可接受后开启）；② MCP 桥重构为进程常驻宿主 + 每轮租约轮换工具注册（消除每轮 Kestrel 构建与死端点热重连）；③ 准备链路并行化（历史/工具发现/feature 工厂 `Task.WhenAll`，失败统一释放）；④ `SkillPackageCache` 内容寻址 Skill 包缓存；⑤ MCP/Runtime profile 仓储内 30s TTL 读缓存；⑥ 仓库不再固定 OpenCode 版本。验证与实测见 [记录](../test-reports/2026-10-21-opencode-keepalive.md)。
- [ ] **ACP-R05 OpenCode 进程环境隔离跟进**（待分派）：实测 Windows + OpenCode 1.19.3 下子进程无视平台设置的 HOME/USERPROFILE，扫描并加载宿主用户 `.claude/skills`、`.agents/skills`（26 个技能进入上下文）。需上游确认 homedir 解析机制或平台侧进一步收紧；同项跟进空闲进程持续外呼与 CPU 空转（阻碍保活默认开启）。证据见 [记录](../test-reports/2026-10-21-opencode-keepalive.md)。

本轮已按评审意见移除 MAF 推理栈（Application 内 Microsoft.Agents.AI 全系引用、进程内推理循环与平台侧 MCP 工具代理，原 A09 的 Application 部分），并支持外部 MCP server 经 ACP session/new 透传、Skill 工作区物化。Runtime 由独立设置页管理，聊天顶部选择已保存的 Runtime 和模型连接；已移除 Agent 编辑器中的 OpenCode 配置，补充实际 ACP 握手身份的展示与持久化。不代表 A02 的并发容量/隔离攻击验证、A04 Worker、A05 分布式 RunGrant、A06 事件重放或 A08 人工审批已完成；原任务保留未完成状态，具体验收边界以实测记录为准。

## P0：验证与准备

- [ ] **A01 .NET 10 升级**（待验证：`dev` 本地构建与全部测试已通过，待 CI 与镜像构建验证；须在 2026-11-10 前完成）。主责：`global.json`、`Directory.Build.props`、`Directory.Packages.props`；与功能改动分开提交。
  验收：全解决方案构建与测试通过（本地已通过）；MCP SDK、EF Core、Microsoft.Agents.AI 在 .NET 10 下确认可用（构建与测试已覆盖）；GitHub Actions CI 通过；Engine.Host、Router、Runner 三个镜像在 Docker 下构建并启动。C# 14 的 span 重载变化已在 `RedisConversationCache`、`AgentFactory` 处修正。Serilog.AspNetCore 暂留 8.0.0，Swashbuckle 暂留 6.6.2。
- [ ] **A02 ACP 端到端 spike**（待分派）。主责：新建临时 spike 项目，不改现有 Application/Engine；以 OpenCode 为首个 harness。
  验收：走通领任务、启动 harness、事件回传、经 MCP 调一个 C# 工具、取消；验证 ACP 协议库的会话恢复、反向权限、断线取消；实测单会话冷启动耗时与单机并发上限；记录 harness 自带沙箱的隔离反例（同名会话、越界路径、HOME 泄漏）。可从 `origin/codex/runtime-adapters-acp`（PR #229）逐块移植协议帧、失败分类与事件映射，不整体合并。
- [ ] **A03 提案评审与 ADR 拆分**（待分派，依赖 A02 结论）。主责：`docs/architecture`、`docs/decisions`。
  验收：提案补齐 MAF 移除顺序与前端兼容层退出条件后批准；第 8 章决策拆成 ADR（重复的 `0004` 编号已修正为 `0005`）；批准后按新执行行为更新 overview 与模块文档；R01 已授权的目录与边界同步不受此限制。

## P1：新架构落地（依赖 A03）

- [ ] **A04 Runtime 契约与 Worker**（待分派）。主责：Contracts/Runtime、新建 Runtime.Acp、Worker.Host；契约与协议实现先行，不改变现有 Application 的 MAF 执行路径。
  验收：IRuntimeAdapter、RuntimeEvent、RunSpec、Worker 协议契约；假 harness 测试覆盖协议、断线、取消；Worker 租约失联后自停。
- [ ] **A05 MCP 网关与 RunGrant**（待分派，吸收原 F03）。主责：在现有 Capabilities 基础上新增 MCP 网关能力（首版不拆 Host 项目）；交界：Engine/Hosting、Engine.Host。
  验收：RunGrant 签发与验签、调用主体不依赖模型参数、跨租户凭证不复用、凭证可轮换与撤销、认证失败可审计；Capabilities 中的文件/RAG/工具业务及 Application 中的 Skill 适配，按新的授权边界接入 MCP 工具；现有模块抽取不等于网关已经实现。
- [ ] **A06 Application 与事件持久化**（待分派，吸收原 F01 的记录与重放目标）。主责：在现有 Application 上新增运行与事件用例；协调 Application/Conversation、Capabilities/Security 和 Framework 持久化实现。
  验收：准入与能力匹配、Run/Session 状态机、会话租约与 fencing、事件按粒度入库并可重放、异常带完整调用阶段与 Tenant/User/Conversation/Trace/Agent 关联、凭证脱敏。
- [ ] **A07 旧聊天接口兼容**（待分派）。主责：Engine.Host/Endpoints/ChatCompat。
  验收：RuntimeEvent 映射为现有前端 SSE；契约测试锁定当前事件形态；取消、刷新重建、会话列表行为不变。
- [ ] **A08 审批**（待分派，取代原 F07）。主责：Application、Engine.Host/Endpoints/Approvals；交界：前端 chat。
  验收：ACP 权限请求映射为待审批状态；批准、拒绝、超时、取消、重启续接都有明确状态；批准前不执行风险操作。

## P2：切换与移除

- [ ] **A09 切流并移除 MAF 路径**（部分完成：Application 的 MAF 推理栈与 Microsoft.Agents.AI 包引用已随 #234 移除；切流验收与依赖项 A04–A08 仍待完成）。主责：Application/Execution、ModelProviders。
  验收：全部请求走 harness 路径且回归通过；移除 Application 中被新执行链替代的 MAF 代码与包，保留平台用例与契约；Architecture.Tests 增加新依赖规则；`OpenAgent.Runner` 不受影响。

## 保留的独立任务

- [ ] **F04 MCP/Skill/Agent 统一授权**（待分派）。主责：Contracts、Capabilities/Security；与 A05 共定主体、资源、动作及租户范围。
  验收：能力发现和实际调用均检查权限；覆盖越权、跨租户、权限撤销与默认策略；目录可见性与执行权限一致，审计可追溯。参考 [授权文档](../modules/security/permission/README.md)。
- [ ] **F05 生产容灾备份**（待分派，生产上线前必须验收）。主责：数据库与部署运维；交界：Framework、对象存储、配置密钥。
  验收：确定 RPO/RTO、保留周期和恢复责任人；自动备份 PostgreSQL，并明确 S3 文件与 Data Protection 密钥的配套恢复；失败告警、恢复脚本和实际恢复演练记录。
- [ ] **F06 Workflow 多 Agent 编排**（暂缓，待 A03 后按新架构重新评估）。不得将现有 `workflow` 路由占位当成已部署服务。关联 [Issue #150](https://github.com/philfanzhou/OpenAgent/issues/150)。

## 与 MAF 无关的结构拆分

以下任务不涉及要移除的执行路径，可独立推进；目标目录不表示已经存在。

- [ ] **S02 / T11** — Host/Extensions 端点归 Endpoints 对应域与 Admin/{Agents,Models,Mcp,Rag,Skills}；流式 writer/headers/heartbeat/payload 归 Streaming
  依赖与验收：协调 I03；保留 Map* 汇总、认证、Development-only 管理映射及 HTTP/SSE 顺序与 done
- [ ] **S03 / T12** — Host/Skills/SkillPackageManagementService 归 Engine/Management/Skills；Contracts 提供纯命令/结果，Host 只映射 HTTP
  依赖与验收：依赖 S02；Engine internal 实现；验证上传更新删除、绑定、版本冲突与失败清理
- [ ] **S04 / T13** — Host/Files 的 S3FileObjectStore、S3Presigner、存储 Options/Validator 归 Framework/ObjectStorage；健康检查归 Host/Health
  依赖与验收：可独立推进；AWSSDK.S3 同迁、Host 注册门面保留且不双注册；上传下载、签名、校验与 readiness 一致
- [ ] **S05 / T14** — Framework/Persistence/OpenAgentDbContext.OnModelCreating 拆为 Mapping 下每实体 IEntityTypeConfiguration
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
