# 任务清单

状态核对：2026-10-16，ACP 任务基于 `dev` `30fde6d5010fc2e57208a95912d7fa7600eaa3f9`。本页是当前待办的唯一入口；历史功能方向以[新架构提案](../architecture/new-architecture.html)（Proposal，未批准）为准，模块实际目录见 [开发指南](../overview/DevelopmentGuide.md)，旧方案见 [历史归档](../archive/README.md)。

## 状态与分派规则

- `[x]` 表示已合并并有验收记录；`[ ]` 表示仍有工作。下列未完成任务默认状态为**待开发**、负责人为**未分配**，不能用已有 PR 代替分派或验收。
- 领取任务时在该项补充“负责人 / 状态（待开发、开发中、待评审、待验证、阻塞） / 实现 PR / 验收证据”；有阻塞需记录原因与依赖 ID。
- 每项独立 PR；写明拥有目录、共享文件、契约、验证和回退。公共 DTO、Host Program、项目/包引用、数据库模型/迁移及前端 app 由集成人协调。同一文件的迁移先合入共同基线。
- 后端范围以下以 `Backend/src/OpenAgent.*` 为根，前端以 `Frontend/OpenAgent.Chat/src/` 为根。结构迁移保持协议和行为兼容；功能/schema 变化单独验收及制定回退方案。

## ACP 单包 SDK 分阶段任务

本组更新：2026-10-16。SDK 固定为 `Backend/src/OpenAgent.Runtime.Acp`，一个可发布包；共同拥有 `Backend/tests/OpenAgent.Runtime.Acp.Tests` 与 `Backend/tools/OpenAgent.Runtime.Acp.TestConsole`。阶段任务细节见 [实现任务](../integrations/acp-dotnet-sdk/IMPLEMENTATION.md)，公共行为见 [主规格](../integrations/acp-dotnet-sdk/SPEC.md)。每个能力必须同步 SDK、测试、控制台及文档。

以下均未合并，因此保留 `[ ]`；P0～P7 是 ACP 的阶段号，与本页优先级章节不同。所有阶段变更基于 `dev`，PR base 为 `dev`，继承 .NET 10 集中配置。共享文件由技术负责人协调：Backend/OpenAgent.sln、AGENTS.md、开发指南及集中包配置；不改变既有平台依赖白名单，不接入生产执行链。

| 任务 / 阶段 | 优先级 | 负责人 / 状态 | 前置与验收 |
|---|---|---|---|
| [ ] ACP-00 / P0 工程及传输基线 | P0 | 质量安全工程师（OPEN-7）/ 独立复审与技术核对通过，待人工验收 | 三个工程、49 个传输/生命周期 + 1 个依赖 + 5 个控制台进程测试；主解回归，见下方记录 |
| [ ] ACP-01 / P1 协议模型与生成 | P0 | 后端工程师（OPEN-8）/ 待评审，[PR #232](https://github.com/philfanzhou/OpenAgent/pull/232) | ACP-00 技术门槛通过；固定 schema、可重复生成、契约夹具、schema 控制台及报告测试 |
| [ ] ACP-02 / P2 双向 RPC | P0 | 后端工程师（OPEN-9）/ 开发中（自研引擎与 rpc/reverse 场景已实现；fault 场景与 QuotaShutdown 测试类在近期引擎改动后仍有超时/挂起，修复中；分支推送受环境网络阻塞，本地提交 `ced12727` 待推送） | ACP-01；引擎探针、可重入关联/取消/关闭，rpc/reverse/fault 场景与异常测试 |
| [ ] ACP-03 / P3 双侧最小闭环 | P0 | 后端工程师（OPEN-10）/ backlog，待前置验收 | ACP-02；握手/new/prompt/update/cancel，Session/Turn 与真实 stdio 控制台测试 |
| [ ] ACP-04 / P4 进程接入 | P1 | 后端工程师（OPEN-11）/ backlog，待前置验收 | ACP-03；Processes 生命周期、外部 Agent 启动选项、stderr/退出/所有权测试 |
| [ ] ACP-05 / P5 会话与配置 | P1 | 后端工程师（OPEN-12）/ backlog，待前置验收 | ACP-04；完整会话、配置与 14 类更新；session/config/updates 场景及顺序/恢复测试 |
| [ ] ACP-06 / P6 交互能力 | P1 | 后端工程师（OPEN-13）/ backlog，待前置验收 | ACP-05；按权限→文件→终端→elicitation→认证逐项完成 SDK、测试和对应场景 |
| [ ] ACP-07 / P7 发布验收 | P2 | 质量安全工程师（OPEN-14）/ backlog，待前置验收 | ACP-06；跨语言、负载、AOT、包/API/文档，interop/load 场景及完整矩阵 |

ACP-00：OPEN-7 在固定 SHA `3be97c118f87cd714012bc5c0889dcfe2ad7b4c0` 独立复审关闭 F1–F4，无新增阻断；技术负责人已核对原始证据、38 个变更文件哈希及最终 PR 范围，接受 P0 工程与传输基线。独立实测主解 974 通过、0 失败、24 门控跳过，ACP 55 通过；原探针 27/27、补充探针 123/123，见 [验证记录](../test-reports/2026-10-16-acp-dev-baseline-import.md)。试验代码/历史调研未恢复。草稿 [PR #231](https://github.com/philfanzhou/OpenAgent/pull/231) 指向 dev，本次交接仅更新验收记录，Backend 与复审 SHA 相同。仅放行 OPEN-8，OPEN-9 至 OPEN-14 保持 backlog；OPEN-7 保留 in_review 等待人工验收，未合并项继续保留 `[ ]`。

ACP-01：实现提交 `71bf602a5ab04bf962b8d6dac456b8587bab8526` 固定稳定 v1 原件/lock/许可，交付 178 定义、25 方法、14 更新、同包 DTO/context/RpcId/错误与 schema 控制台；首轮独立复核（OPEN-15）判「不通过，1 项阻断」（F-A 信封联合首个匹配静默丢字段，另有 F-B~F-F）。修复提交按信封 `method` 判别 `params`、响应 `result` 逐字保留并强制 `result`/`error` 互斥、`AuthMethod` 未知 `type` 显式拒绝（生成器钉 `type:"agent"` 常量）、补 45 个方法方向信封夹具与每例 `required` 元数据（现 352 夹具，`fixtures/build.py` 确定性重建）、文档交代运行时 323 定义 = 178 固定 + 145 合成及目录边界；模型覆盖不表示 RPC 已可调用。主解实测 1522 通过、0 失败、24 门控跳过，ACP 603 通过；608 生成文件连续两次无差异，5/5 篡改探针拒绝，Release 单包检查通过。见[验证报告](../test-reports/2026-10-16-acp-p1-models-codegen.md)及 OPEN-8 原始证据附件。信封读取语义与新公共 API 边界仍待技术负责人及独立复核；保留原有审计警告与 P7 发布项。PR 包含未合并 P0，未合并/发包/部署；只更新本阶段交付，后续阶段继续 backlog。

## 已完成基线

- [x] **B01 Core 第一批边界**：功能归域、模块 DI、MCP/Skill 扩展接口、Runner 客户端及产物发布复用、测试目录与依赖门禁。证据：[PR #158](https://github.com/philfanzhou/OpenAgent/pull/158)、[验证记录](../test-reports/2026-10-08-module-boundaries.md)。
- [x] **B02 前端第一批边界**：shared transport/wire/SSE、业务 API 归域、模型/MCP/Skill/RAG 设置模块与 import 门禁。证据同 B01。第一批边界不代表下列功能已完成。

## 当前结构整理（独立于 ACP 功能迁移）

用户已批准按 [新架构方案的目录图](../architecture/新架构方案.html) 重组源码并保留现有行为：Core → Application，Infrastructure → Framework，Hosting 合并 Engine；独立能力迁入 Capabilities，MAF/MCP 适配继续由 Application 执行。不新增 ACP/Worker/审批/RunGrant 功能，不改数据库 schema。

- **R01 目录与边界重组**：实现与本地验证完成；交付分支为 `dev`，尚未合并至 `main`，远端 CI 与部署未验收；覆盖源码、namespace、项目引用、架构门禁、测试归属与 CI/规则路径。迁移前：917 通过、24 跳过；迁移后：919 通过、24 跳过、0 失败，完整构建 0 警告、0 错误。证据：[验收记录](../test-reports/2026-10-15-directory-restructure.md)。
- 下列 A 系列仍是尚未批准的功能规划，不作为本次目录迁移的前置门禁。下文主责路径已按当前模块更新；未来 ACP 切换和 MAF 删除须另行批准。

## 方向变更

执行路径由 MAF 改为“平台控制面 + Worker + 可换 ACP harness”，MAF 执行路径不保留，沙箱使用 harness 自带机制（如 Codex CLI），`OpenAgent.Runner` 暂不处理。旧清单中依赖 MAF 的任务已取代或并入下列 A 系列；后续功能任务按 A 系列评审；本次已授权的行为保持式结构整理不受 A03 限制。

## P0：验证与准备

- [ ] **A01 .NET 10 升级**（待验证：`dev` 本地构建与全部测试已通过，待 CI 与镜像构建验证；须在 2026-11-10 前完成）。主责：`global.json`、`Directory.Build.props`、`Directory.Packages.props`；与功能改动分开提交。
  验收：全解决方案构建与测试通过（本地已通过）；MCP SDK、EF Core、Microsoft.Agents.AI 在 .NET 10 下确认可用（构建与测试已覆盖）；GitHub Actions CI 通过；Engine.Host、Router、Runner 三个镜像在 Docker 下构建并启动。C# 14 的 span 重载变化已在 `RedisConversationCache`、`AgentFactory` 处修正。Serilog.AspNetCore 暂留 8.0.0，Swashbuckle 暂留 6.6.2。
- [ ] **A02 ACP 端到端 spike**（待分派）。主责：新建临时 spike 项目，不改现有 Application/Engine；以 OpenCode 为首个 harness。
  验收：走通领任务、启动 harness、事件回传、经 MCP 调一个 C# 工具、取消；验证 ACP 协议库的会话恢复、反向权限、断线取消；实测单会话冷启动耗时与单机并发上限；记录 harness 自带沙箱的隔离反例（同名会话、越界路径、HOME 泄漏）。可从 `origin/codex/runtime-adapters-acp`（PR #229）逐块移植协议帧、失败分类与事件映射，不整体合并。
- [ ] **A03 提案评审与 ADR 拆分**（待分派，依赖 A02 结论）。主责：`docs/architecture`、`docs/decisions`。
  验收：提案补齐 MAF 移除顺序与前端兼容层退出条件后批准；第 8 章决策拆成 ADR，同时修正两个 `0004` 重复编号；批准后按新执行行为更新 overview 与模块文档；R01 已授权的目录与边界同步不受此限制。

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

- [ ] **A09 切流并移除 MAF 路径**（待分派，依赖 A04–A08 验收及真实 harness 验证）。主责：Application/Execution、ModelProviders、MAF 适配及 Microsoft.Agents.AI 包引用。
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
