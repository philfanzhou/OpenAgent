# 任务清单

状态核对：2026-10-21；SDK PR #233 与 OpenCode 运行时 PR #234 均已合并到 `dev`。本页是当前待办的唯一入口；历史功能方向以[新架构提案](../planning/architecture/new-architecture.html)（Proposal，未批准）为准，模块实际目录见 [开发指南](../overview/DevelopmentGuide.md)。

## 状态与分派规则

- `[x]` 表示已合并并有验收记录；`[ ]` 表示仍有工作。下列未完成任务默认状态为**待开发**、负责人为**未分配**，不能用已有 PR 代替分派或验收。
- 领取任务时在该项补充“负责人 / 状态（待开发、开发中、待评审、待验证、阻塞） / 实现 PR / 验收证据”；有阻塞需记录原因与依赖 ID。
- 每项独立 PR；写明拥有目录、共享文件、契约、验证和回退。公共 DTO、Host Program、项目/包引用、数据库模型/迁移及前端 app 由集成人协调。同一文件的迁移先合入共同基线。
- 后端范围以下以 `Backend/src/OpenAgent.*` 为根，前端以 `Frontend/OpenAgent.Chat/src/` 为根。结构迁移保持协议和行为兼容；功能/schema 变化单独验收及制定回退方案。

## OpenAgent 稳定性修复计划（2026-11-25）

**实施范围仅限 11 月 24 日邮件《OpenAgent平台开发任务支持》及其两份 Markdown 附件明确提出的修复和验收项。** 条目编号采用详细版，首批顺序采用简要版的建议。每处改动对应到附件条目及其验收条件。关联 GitHub Issue 和 PR 用于核对当前状态、已合入内容及重叠范围。GitHub `dev` 核对基线为 `87319d1e`。当前修复在本地 worktree，等待推送确认。 [dev 基线](https://github.com/philfanzhou/OpenAgent/commit/87319d1e)

### 实施顺序

1. **首批先做第 2 项**。用本地 HTTP 和 OTLP 接收端建立审计回归测试，再补告警。第 3 项可先核对事件来源并确认去重规则。第 5 项可先做导出监测，本地补传演练通过后再安排测试环境切换。
2. **第二批补齐已知缺口**。优先复核第 4 项的 S3 故障路径和第 6、7 项与 PR #308 的分工。第 8、9、11、12、13 项按各自前置条件拆分交付。第 10 项先量化容量和恢复成本，清理策略另行确认。
3. **第 1 项先做适配器验证**。验证 Pi 的 ACP 与平台工具能力后再做产品接入。上线验收依赖第 2、3 项及第 4 项的归档故障隔离。

### 首批审计修复

| 附件项 | 状态与主责范围 | 实施与验收 |
|---|---|---|
| **2 · 轮次审计身份防回归** | 建议首做，待确认。Application/Observability、Application/Execution、Engine.Host 的续播端点及 Engine.Tests。`f22d7ec5` 已修复生产者 span 的导出，仍缺失败告警和真实导出验证。 [修复提交](https://github.com/philfanzhou/OpenAgent/commit/f22d7ec5) · [相关 Issue #267](https://github.com/philfanzhou/OpenAgent/issues/267) | 执行步骤见下文。复用生产宿主的 OTLP 注册与现有 HTTP 测试夹具。 |
| **3 · 原生工具和外部 MCP 审计** | 待确认平台工具去重规则。Application/Observability 与 Runtime 工具事件。现有 `OpenAgent` 工具源和 Engine 的订阅不匹配，待结束记录跨轮次共享。 [相关 Issue #278](https://github.com/philfanzhou/OpenAgent/issues/278) | 通过宿主已订阅的源导出，按轮次及调用 ID 配对和收尾。单测仅监听宿主实际订阅的源，并验证审计源均在宿主订阅列表内。实测原生、外部 MCP、平台工具、同 ID 并发轮次、取消和异常。校验孤立结果不生成记录，平台工具每次调用只显示一条，属性名与脱敏规则兼容审计网关。 |
| **5 · 审计导出告警与补传** | 待开发。Engine/Hosting、Collector 和部署配置。现有 PR #313 面向 `main`，涉及 OTLP 信号、认证和关联。 [相关 PR #313](https://github.com/philfanzhou/OpenAgent/pull/313) | 先验证 trace 导出失败的限频告警与计数，再配置持久队列、重试、容量与队列监控。使用 traces 专用端点，保留网关协议和认证契约。测试环境模拟网关离线、产生会话和工具记录、重启 Collector、恢复网关，逐条核对补传与归属。上线前确认数据目录访问权限、容量和保留期。同步清理无效环境变量和无来源订阅。回退时让 Engine 和 Router 重新直连原网关。 |

### 第 2 项实施与验收

1. 复用 `Backend/tests/OpenAgent.Engine.Tests/Hosting/OtlpLogExportIntegrationTests.cs` 的接收端和现有 SSE 测试夹具，用真实聊天 HTTP 路由、生产宿主的 tracing 注册和 fake runtime。解析收到的 OTLP traces，断言 `OpenAgent.ConversationRun` 来自 `OpenAgent.Engine`，携带会话和用户标签，与聊天 HTTP span 同属一个 TraceId，且该 HTTP span 是其直接父级。
2. 在测试工作区临时将生产者换回事故时的裸 `new Activity()`，确认上一步因为收不到有效的生产者 span 失败。撤回实验性改动后重新跑通。
3. 让 `Backend/src/OpenAgent.Application/Observability/AuditActivityEnrichment.cs` 的 `Publish` 报告身份写入结果。`RuntimeAgentExecutor` 仅在宿主配置了 traces 导出且写入失败时，记录有固定 EventId 的限频 Warning。验证健康轮次、未配置导出、无 Activity、裸 Activity 及连续失败，确认失败告警不影响聊天响应。实际导出由接收端测试判定。
4. 发真实工具桥请求，验证 `/runtime-tools` 的 HTTP span 有会话 ID 与工具名。为已通过归属校验的续播请求补会话标签，验证 `GET /api/v1/agent/conversations/{conversationId}/stream` 实际导出该标签。跨服务父子关系由第 11 项验收。
5. 运行 Application、Engine 的相关测试及架构门禁，记录命令、结果和反例。回退以本项 PR 为单位。涉及生产的审计与 Auto 检查在部署后单独记录。

### 其余任务与前置条件

| 附件项 | 状态与主责范围 | 实施与验收 |
|---|---|---|
| 1 · Pi 运行时 | 待调研。Runtime、Engine 的运行时配置和 Chat 选择入口。上线前完成第 2、3 项及第 4 项归档故障验收。 | 固定 Pi 和适配器版本，先验证握手、取消、续聊与工具桥。允许用户选择运行时，默认仍为 OpenCode。用同一套 Agent 验证两种运行时的聊天、工具、文件和审计，再验已有会话切换后保留会话 ID、历史及附件，且不重放历史工具。 |
| 4 · 自动归档故障隔离 | 部分合入。Capabilities/Files、Runtime 和 Engine.Tests。#320 已隔离部分保存和恢复失败，`FileExchangeContext.SaveOutputsAsync` 仍需验证对象存储写入故障与独立时间预算。 [PR #320](https://github.com/philfanzhou/OpenAgent/pull/320) | 用真实 S3 兼容存储注入写入失败、超时及恢复，验证 SSE 正常结束、正文持久化、失败附件无下载链接、显式发布明确失败且恢复后可重试。另验用户取消语义。 |
| 6 · ACL 数据不可用 | 待开发。Router/Security、Agent 列表和选择入口。PR #308 未涉及 Router 文件。 [Issue #265](https://github.com/philfanzhou/OpenAgent/issues/265) · [PR #308](https://github.com/philfanzhou/OpenAgent/pull/308) | 区分无 ACL、拒绝和读取故障。故障时使用有效快照，缺快照时明确返回 503。验证冷缓存、短暂故障、快照过期和撤权，核对列表、显式选择、Auto 候选与 fallback 的权限一致性。 |
| 7 · Engine 直连授权 | 待核对。部署入口、Router 与 Engine.Host。先复核 PR #308 的执行授权覆盖，以及生产允许直连的端点。 [Issue #265](https://github.com/philfanzhou/OpenAgent/issues/265) · [PR #308](https://github.com/philfanzhou/OpenAgent/pull/308) | 明确 Agent 授权规则由谁执行。用同一身份验证 Router 和获准的直连入口，允许与拒绝结果一致，文件分享和服务间接口继续可用。 |
| 8 · `intern-decision` 稳定与安全 | 待开发。Router/Providers、Agent 选择及部署配置。调整默认地址前先设置并验证目标环境的显式配置。 | 复用受控 HTTP handler，收敛超时，添加有界熔断，保护日志与授权候选。用本地假服务验证重定向、故障、恢复、超时与取消，验证 Auto 正常选路及故障时的授权回退。 |
| 9 · Router 容器降权 | 待开发。Router 镜像、生产与预览 Compose。第 13 项的 Router 子项归此交付。 | 验证非 root、能力移除、提权限制和只读根文件系统。按需配置可写路径，实测健康、转发、Auto 与 SSE。 |
| 10 · 文件容量与保留 | 先度量，待业务和运维确认保留及删除策略。Capabilities/Files、Framework 与对象存储。 [历史 Issue #185](https://github.com/philfanzhou/OpenAgent/issues/185) | 先量化新增量、占用量、恢复耗时和查询上限。规则确认后验证过期无引用文件可清理，已发布文件、有效分享及续聊素材仍可访问。 |
| 11 · 跨组件追踪关联 | 待验证。Router 转发、Engine.Host、Runtime 工具桥。先确认审计网关的归集规则，第 2 项负责续播会话标签。 [相关 Issue #267](https://github.com/philfanzhou/OpenAgent/issues/267) | 用 OTLP 记录实际父子关系再补上下文传播和追踪 ID 校验。真实链路验证 Router、Engine 和桥属于同一 trace，并发及连续轮次归属正确，现有审计标签兼容。 |
| 12 · Router 运行问题 | 待拆分。Router 限流、SSE 转发、实例隔离、Router 与 Engine 的角色解析。 | 分别验证各入口限流不影响正常轮询及上传，有心跳的 SSE 可完成，单次故障不误摘除实例，持续故障可隔离并恢复。角色规则先对照实际 IdP 声明，再验证两端对同一令牌的结果。 |
| 13 · Redis 与 Keycloak 配置 | 待安排维护窗口。基础设施 Compose。Router 降权归第 9 项，无效 OTLP 环境变量归第 5 项。 | 明确所有 Redis 客户端和备份恢复步骤，再统一切换认证、收紧网络暴露与注册表地址范围。Keycloak 管理员凭据由部署配置提供。切换后验证内部连接、路由、权限及 Auto。 |

### 验收记录

下表记录当前交付状态。上面的计划表保留邮件核对时的起点。

| 邮件条目 | 本地验证 | 待验收或待决定 |
|---|---|---|
| 1 · Pi | 真实适配器 0.1.8 通过建会、关闭、平台历史重放、图片输入、HTTP MCP 调用、流式正文和取消。真实 OpenCode 和 Pi 连接同一个回环模型，已验证在同一个 Engine 会话切换后保留历史及 Agent，旧工具调用未重复执行。试点镜像已本地构建并核对版本。 | 试点环境待验收实际模型、文件交付、压缩后续聊及审计结果。 |
| 2、3、5、11 · 审计 | 本地 OTLP 检查 Router 到 Engine 和对话轮次的父子关系、聊天、续播、原生工具、外部 MCP、工具桥和导出失败告警及计数。Collector 在网关离线时接收两条测试记录，重建后补传成功。 | 在真实审计台核对平台工具去重、脱敏和会话归集。Collector 切换前由运维确定队列容量与保留期。 |
| 4 · 自动归档 | 隔离 SeaweedFS 的真实 S3 拒绝写入时，SSE 正常结束且正文持久化，文件保持未关联。恢复写权限后存储对象读取成功。独立时限和调用者取消均有回归。 | 部署后随业务链路验收。 |
| 6、7、8、9、12、13 · Router 与部署 | ACL 快照和熔断由本地测试验证。Nginx、Router 和 Redis 容器用同一身份验证两个公开入口均执行 Agent ACL，分享仍可访问；另实测 Auto 和 SSE 转发。Router 实际转发测试覆盖空闲连接释放、心跳保活、31 MiB 上传、会话轮询及分类限流。容器 UID 非 0、有效 capabilities 为 0、禁止提权且根目录只读。Redis 拒绝未认证请求、接受认证 PING 且无宿主端口。 | 生产入口授权、实际 IdP 声明、决策服务合同、Redis 切换及 Keycloak 现有账号轮换，需要对应环境和维护窗口。 |
| 10 · 文件保留 | 25 个历史成品仅查询 8 个候选并读取 2 个对象。记录上传数量、字节数和恢复耗时。容量盘点查询已在 PostgreSQL 验证消息、有效分享、续聊、无引用成品和其他资产。 [容量与保留盘点](../database/tables/FileAssets.md#容量与保留盘点) | 实际存量占用由对象存储观测。删除对象需先确定保留期限、引用标准和清理规则。 |

每个 PR 记录负责人、基线、改动范围、本地验证命令、结果和回退方式。涉及 Engine、Runtime 或 Router 的发布在获准的环境执行附件附 A 的回归。发送一条含工具调用的消息，确认审计库收到新会话与工具记录并在审计台可见，再新建 Auto 会话验证实际路由。生产验收结果在发布后单独登记。 [部署与回退](../operations/deployment.md)

## 已完成基线

- [x] **B01 Core 第一批边界**：功能归域、模块 DI、MCP/Skill 扩展接口、Runner 客户端及产物发布复用、测试目录与依赖门禁。证据：[PR #158](https://github.com/philfanzhou/OpenAgent/pull/158)。
- [x] **B02 前端第一批边界**：shared transport/wire/SSE、业务 API 归域、模型/MCP/Skill/RAG 设置模块与 import 门禁。证据同 B01。第一批边界不代表下列功能已完成。

## 当前结构整理（独立于 ACP 功能迁移）

用户已批准按 [新架构方案的目录图](../planning/architecture/新架构方案.html) 重组源码并保留现有行为：Core → Application，Infrastructure → Framework，Hosting 合并 Engine；独立能力迁入 Capabilities，MAF/MCP 适配继续由 Application 执行。不新增 ACP/Worker/审批/RunGrant 功能，不改数据库 schema。

- **R01 目录与边界重组**：实现与本地验证完成；交付分支为 `dev`，尚未合并至 `main`，远端 CI 与部署未验收；覆盖源码、namespace、项目引用、架构门禁、测试归属与 CI/规则路径。迁移前：917 通过、24 跳过；迁移后：919 通过、24 跳过、0 失败，完整构建 0 警告、0 错误。
- 下列 A 系列仍是尚未批准的功能规划，不作为本次目录迁移的前置门禁。下文主责路径已按当前模块更新；2026-10-19 用户另行授权本轮 OpenCode ACP 聊天执行切换；Worker/RunGrant 和彻底删除 MAF 包仍按 A 系列单独验收。

## 方向变更

执行路径由 MAF 改为“平台控制面 + Worker + 可换 ACP harness”，MAF 执行路径不保留，沙箱使用 harness 自带机制（如 Codex CLI）。2026-10-28 用户授权移除独立 Runner：`OpenAgent.Runner` 项目、Runner 客户端、`execute_code`/workspace 文件工具/`run_skill_script`、`CodeExecution` 配置开关（含 `CodeExecutionJson` 列，新增迁移 `20261028091823_RemoveCodeExecutionConfiguration`）与部署/CI 面一并删除；代码执行交给 OpenCode 原生工具（宿主经 `OpenCode:AllowedNativeTools` 放行，默认仍全拒绝）。旧清单中依赖 MAF 的任务已取代或并入下列 A 系列；后续功能任务按 A 系列评审；本次已授权的行为保持式结构整理不受 A03 限制。

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

用户已指定 OpenCode，并授权在第二个 PR 切换聊天执行入口。负责人：Codex；状态：已合并；实现 PR：[#234](https://github.com/philfanzhou/OpenAgent/pull/234)（已改基到 `dev`）；实现与验收随 PR #234。

- [ ] **ACP-R01 平台契约与默认执行入口**：Contracts/Runtime + Application/Execution；平台保留授权、历史和持久化，不引用 ACP 公共类型。
- [ ] **ACP-R02 OpenCode 驱动与工具桥**：Runtime 类库（不打包）；固定版本 ACP 进程、隔离环境、每轮 loopback MCP bearer、工具预算及事件合并。
- [ ] **ACP-R03 验证与交付**：真实进程/本地模型/平台 C# 工具、取消、历史续聊、HTTP/SSE；补齐配置、镜像与文档；已移除 MAF 回归快照，补充免 Docker 本机组合、前端默认模型选择、真实 Skill/MCP 原生调用与真实 Anthropic 兼容模型验收。
- [ ] **ACP-R04 运行时延迟优化**（工作区与原生 session 复用已本地验证，待 [PR #297](https://github.com/philfanzhou/OpenAgent/pull/297) 评审）：进程与工作区统一由 `OpenCodeProcessKeeper` 创建、复用和回收；同一归属/配置/Skill 内容复用固定 cwd，检查点一致时通过 `session/resume` 延续原生历史，每轮轮换工具 bearer；平台提交成功后才确认可复用。默认空闲保留 300 秒、最多 16 个空闲会话，可设 0 关闭。保留已有并行准备、Skill/配置缓存与共享 MCP 桥。实现与本地验证见 [OpenCode 运行时](../integrations/opencode-runtime.md)；版本/压缩/工具 schema 变化、取消或提交失败均重建/回收；简要说明见 [简要调研](architecture/acp-latency.md)。
- [ ] **ACP-R05 OpenCode 进程环境隔离跟进**（N02 实现待评审；最终多租户隔离待 N16.2 部署验收）：实测 Windows + OpenCode 1.19.3 下子进程无视平台设置的 HOME/USERPROFILE，扫描并加载宿主用户 `.claude/skills`、`.agents/skills`（26 个技能进入上下文）。需上游确认 homedir 解析机制或平台侧进一步收紧；同项跟进空闲进程持续外呼与 CPU 空转（当前按时限与数量上限保活）。证据为 PR #243 的实测结论。
- [ ] **ACP-R06 镜像版本锁定与发布流水线**（待评审）：`Backend/opencode.version` 作为内嵌 OpenCode 版本的单一事实源（当前 1.19.3），`scripts/build-images.*` 读取并经 `OPENCODE_VERSION` 注入 Engine 构建、附带版本 label；`v*.*.*` 标签触发 Release 工作流，先以 `--target runtime-tests` 真实进程回归门禁，再把 engine/router/chat 推送 GHCR（Engine 标签带 `-oc<版本>` 后缀）并附 Compose 部署包制品。负责人：ZCode；实现 PR：[#247](https://github.com/philfanzhou/OpenAgent/pull/247)。验收：部署脚本测试 12/12 通过（WSL）；WSL Docker runtime-tests 以钉住 1.19.3 实测通过，镜像 label 核对一致；远端打 tag 的首次发布待验收。

- [ ] **ACP-R07 压缩审计与原生会话持久恢复**（本地实现，未合并/部署）：基线 `dev@8a8172df`，分支 `codex/remove-platform-model-compaction`，负责人 Codex。范围：Contracts 压缩事件、Application 持锁审计、Runtime/OpenCode 标准事件与 v1 原生导出适配、Engine SSE/状态卷、Chat 时间线。平台摘要客户端/策略/手动接口已删除；展示摘要不充当恢复快照。验收与降级规则见 [会话文档](../modules/conversation.md#上下文压缩)。真实 1.19.3 + 本地确定性模型已验证自动压缩后跨进程恢复、缺失/拒绝恢复时回放及取消作废；正式环境、共享存储与 Windows 卷权限待验收。回退必须保留数据库会话记录及状态卷。

本轮已按评审意见移除 MAF 推理栈（Application 内 Microsoft.Agents.AI 全系引用、进程内推理循环与平台侧 MCP 工具代理，原 A09 的 Application 部分），并支持外部 MCP server 经 ACP session/new 透传、Skill 工作区物化。Runtime 由独立设置页管理，聊天顶部选择已保存的 Runtime 和模型连接；已移除 Agent 编辑器中的 OpenCode 配置，补充实际 ACP 握手身份的展示与持久化。不代表 A02 的并发容量/隔离攻击验证、A04 Worker、A05 分布式 RunGrant、A06 事件重放或 A08 人工审批已完成；原任务保留未完成状态，具体验收边界以实测记录为准。

## P0：验证与准备

- [ ] **A01 .NET 10 升级**（待验证：`dev` 本地构建与全部测试已通过，待 CI 与镜像构建验证；须在 2026-11-10 前完成）。主责：`global.json`、`Directory.Build.props`、`Directory.Packages.props`；与功能改动分开提交。
  验收：全解决方案构建与测试通过（本地已通过）；MCP SDK、EF Core、Microsoft.Agents.AI 在 .NET 10 下确认可用（构建与测试已覆盖）；GitHub Actions CI 通过；Engine.Host、Router 两个镜像在 Docker 下构建并启动。C# 14 的 span 重载变化已在 `RedisConversationCache`、`AgentFactory` 处修正。Serilog.AspNetCore 暂留 8.0.0，Swashbuckle 暂留 6.6.2。
- [ ] **A02 ACP 端到端 spike**（待分派）。主责：新建临时 spike 项目，不改现有 Application/Engine；以 OpenCode 为首个 harness。
  验收：走通领任务、启动 harness、事件回传、经 MCP 调一个 C# 工具、取消；验证 ACP 协议库的会话恢复、反向权限、断线取消；实测单会话冷启动耗时与单机并发上限；记录 harness 自带沙箱的隔离反例（同名会话、越界路径、HOME 泄漏）。可从 `origin/codex/runtime-adapters-acp`（PR #229）逐块移植协议帧、失败分类与事件映射，不整体合并。
- [ ] **A03 提案评审与决策落地**（待分派，依赖 A02 结论）。主责：`docs/planning/architecture`（提案）、对应总览/模块文档（决策结论）。
  验收：提案补齐 MAF 移除顺序与前端兼容层退出条件后批准；第 8 章决策结论并入对应总览文档（不再新增 ADR 类型，历史 ADR 已删除，决策以现行文档为准）；批准后按新执行行为更新 overview 与模块文档；R01 已授权的目录与边界同步不受此限制。

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
  验收：全部请求走 harness 路径且回归通过；移除 Application 中被新执行链替代的 MAF 代码与包，保留平台用例与契约；Architecture.Tests 增加新依赖规则；（历史验收口径；Runner 已于 2026-10-28 移除。）

## 保留的独立任务

- [ ] **F04 MCP/Skill/Agent 统一授权**（待分派）。主责：Contracts、Capabilities/Security；与 A05 共定主体、资源、动作及租户范围。
  验收：能力发现和实际调用均检查权限；覆盖越权、跨租户、权限撤销与默认策略；目录可见性与执行权限一致，审计可追溯。参考 [授权文档](../modules/security.md)。
- [ ] **F05 生产容灾备份**（待分派，生产上线前必须验收）。主责：数据库与部署运维；交界：Framework、对象存储、配置密钥。
  验收：确定 RPO/RTO、保留周期和恢复责任人；自动备份 PostgreSQL，并明确 S3 文件与 Data Protection 密钥的配套恢复；失败告警、恢复脚本和实际恢复演练记录。
- [ ] **F06 Workflow 多 Agent 编排**（暂缓，待 A03 后按新架构重新评估）。不得将现有 `workflow` 路由占位当成已部署服务。关联 [Issue #150](https://github.com/philfanzhou/OpenAgent/issues/150)。

## 与 MAF 无关的结构拆分

以下任务不涉及要移除的执行路径，可独立推进；目标目录不表示已经存在。

- [x] **S02 / T11** — Host/Extensions 端点归 Endpoints 对应域与 Admin/{Agents,Models,Mcp,Rag,Skills}；流式 writer/headers/heartbeat/payload 归 Streaming（负责人：zzyyu；已合并；实现 PR：[#301](https://github.com/philfanzhou/OpenAgent/pull/301)；验收证据：路由/认证矩阵由 `MapAgentEndpoints_PreservesBusinessRouteContract` 逐条锁定并通过；SSE headers/心跳/done 终态测试通过；全量构建 0 警告 0 错误，测试 968 通过 / 0 失败 / 28 既有环境依赖跳过；RAG 端点按范围限制原文留在门面文件）
  依赖与验收：协调 I03；保留 Map* 汇总、认证、Development-only 管理映射及 HTTP/SSE 顺序与 done
- [ ] **S03 / T12** — Host/Skills/SkillPackageManagementService 归 Engine/Management/Skills；Contracts 提供纯命令/结果，Host 只映射 HTTP（负责人：zzyyu；状态：待评审；实现 PR：[#303](https://github.com/philfanzhou/OpenAgent/pull/303)；验收证据：服务实现与包解析代码逐字搬运；SkillPackageManagementServiceTests 10 项（上传/安装/更新/删除/版本冲突/失败清理）与 AgentSkillPackageArchiveTests 9 项通过；全量构建 0 警告 0 错误，测试 971 通过 / 0 失败）
  依赖与验收：依赖 S02；Engine internal 实现；验证上传更新删除、绑定、版本冲突与失败清理
- [x] **S04 / T13** — Host/Files 的 S3FileObjectStore、S3Presigner、存储 Options/Validator 归 Framework/ObjectStorage；健康检查归 Host/Health（负责人：zzyyu；已合并；实现 PR：[#299](https://github.com/philfanzhou/OpenAgent/pull/299)；验收证据：实现代码逐字搬运，对象键/元数据/限制/错误信封不变并由迁移后 4 个单测覆盖；全量构建 0 警告 0 错误，测试 968 通过 / 0 失败 / 28 既有环境依赖跳过；本机无 Docker，真实 MinIO 往返待部署环境验证；主干不存在 S3Presigner，无可迁移对象；CI 全绿）
  依赖与验收：可独立推进；AWSSDK.S3 同迁、Host 注册门面保留且不双注册；上传下载、签名、校验与 readiness 一致
- [x] **S05 / T14** — Framework/Persistence/OpenAgentDbContext.OnModelCreating 拆为 Mapping 下每实体 IEntityTypeConfiguration（负责人：zzyyu；已合并；实现 PR：[#298](https://github.com/philfanzhou/OpenAgent/pull/298)；验收证据：`dotnet ef migrations has-pending-model-changes` 报告设计时模型与快照无差异、未生成 migration；全量构建 0 警告 0 错误，测试 824 通过 / 0 失败 / 19 跳过均为依赖真实环境的既有跳过项；CI 全绿）
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
| S11 Runner 拆分 | 取代：Runner 已整体移除，代码执行交给 OpenCode 原生工具（见“方向变更”） |
| S12 剩余域收口 | 取代：验收并入 A09 |
| I01–I06（PR #144、#146、#152–#156） | 取代：均为 MAF 路径；不再合并，处置方式待确认 |
| I07（PR #157 RAG Provider 验证） | 取代：RAG 以 MCP 工具形式落在 A05；PR 处置待确认 |

## 建议执行顺序

1. A01 与 A02 并行；A03 在 A02 结论后评审。
2. A04、A05 纯新增先行，再做 A06、A07；A08 随 A06 之后。
3. A09 最后，F04、F05 与上述并行推进；前端与 Host 拆分按拥有目录独立进行。


## N02 / P0 — OpenCode 宿主继承缓解与隔离边界

- 负责人：Codex；状态：待评审；初始基线：`origin/dev@768a8c1e`，已合入 `origin/dev@45802efd`；实现 PR：[#294](https://github.com/philfanzhou/OpenAgent/pull/294)（草稿）。任务方案来自 2026-10-28 非 RAG 拆解，本检出只补充 N02，不覆盖其他会话的任务细化。
- 范围：Runtime/OpenCode、Runtime.Tests、Engine 开发/镜像配置与 Data Protection 卷挂载。底层原生工具默认拒绝、宿主显式授权原生执行、显式 Skill 路径/名称授权、关闭外部/祖先扫描、私有 profile/tmp、链接拒绝与 Skill 副本、非 root 服务身份。N02 不修改 SDK、依赖白名单或 RAG；Runner 移除随最新 dev 合入。
- 对外行为：开发配置保留显式允许 read/edit/write/bash，Node/Python 实际执行走 OpenCode，Runner 不作为方案或兜底；已有 Unix workspace 需服务账户独占（0700），已有密钥卷需迁移权限。保持 KeepAliveSeconds=0，不启用整个 Worker/RunGrant 架构。
- 验收与反例：[运行时验证和支持模式](../integrations/opencode-runtime.md#n02宿主继承缓解与受支持模式)；部署与回退：[服务身份及密钥卷](../operations/deployment.md#opencode-服务身份与已有密钥卷)。标准 Docker 测试/发布镜像已在本机构建；Windows、实际部署与未来多租户隔离未验收，不能把 N02 缓解标成 A02/A04 全部完成。N03/N04 采用文档中的干净服务身份模式，压测数据只适用于该模式。

## OpenCode 文件收发闭环

方案见 [OpenCode 文件收发](opencode-file-exchange.md)。

- [x] **FX01 原文件输入与历史导入**。已合入 `dev`，见 [#304](https://github.com/philfanzhou/OpenAgent/pull/304)。
- [ ] **FX02 本地产物导出登记**。待合入 `dev`，含输出快照、同轮重试和限额。
- [ ] **FX03 消息发布与下载恢复**。待合入 `dev`，含成功、失败、取消的已发布附件。
- [ ] **FX04 端到端验收与启用文档**。待合入 `dev`，含断线重试、XLSX/PNG 交付和冷启动恢复测试。

## 压测问题修复（LT）

来源：2026-11-26 基于 `dev@9099f7f3` 的隔离压测，工具见 [#321](https://github.com/philfanzhou/OpenAgent/pull/321) 与 [压测工具](../operations/load-testing.md)。负责人：zzyyu；每个工作包一个 PR，自 LT02 起依次堆叠。对比数据来自同一台 WSL2 主机（20 vCPU / 15.5 GB）上的固定评分卡，只适用于该主机。

- [x] **LT01 OpenCode 后台依赖安装空操作**（实现 PR：[#323](https://github.com/philfanzhou/OpenAgent/pull/323)；待合入 `dev`）。`OpenCodeConfigDependencies` 在 XDG 配置目录、`OPENCODE_CONFIG_DIR` 与工作区 `.opencode` 预置空壳，使上游无开关的后台 `@opencode-ai/plugin` 安装成为空操作。验收证据：保活进程空闲 15 秒 CPU 1148% → 15.3%，每会话落盘 244 MB → 7 MB，追问 4 并发吞吐 1.00 → 1.43 轮/秒；对应 ACP-R05 提到的空闲进程 CPU 空转与外呼现象，是否仍有其他外呼未验证。
- [x] **LT02 压缩摘要直接读会话数据库**（实现 PR：[#325](https://github.com/philfanzhou/OpenAgent/pull/325)，堆叠在 LT01 上；待合入 `dev`。[#324](https://github.com/philfanzhou/OpenAgent/pull/324) 是同一问题的另一方案（订阅 OpenCode 事件流），代码上互斥，需选定其一）。原方案「按上下文占用比例决定是否导出」不成立：实测压缩发生在同一轮的提示词处理中途，而 `usage_update` 只在整轮结束后上报一次、报的是压缩之后回落的占用。改为 Engine 以只读连接直接查询原生会话数据库，导出命令只作回退。验收证据：新会话首轮 p50 4158 → 3220 ms，追问 p50 2255 → 1402 ms（收尾 872 → 29 ms），追问 4 并发 p50 2660 → 1455 ms、吞吐 1.43 → 2.61 轮/秒，新会话 8 并发 p50 7302 → 5759 ms；Linux `runtime-tests` 真实进程套件通过，含按真实 `export` 调用次数断言的用例。
- [ ] **LT03 会话状态瘦身与回收**（待开发，依赖 LT02）。
- [ ] **LT04 并发有界排队与断开取消**（待开发，依赖 LT03）。含默认值调整 `MaxConcurrentTurns` 8 → 4、`MaxKeptProcesses` 16 → 4，与 #302 记录的「300 秒 / 16 个空闲会话」不同，需单独确认。
- [ ] **LT05 取消语义与僵尸会话**（待开发，依赖 LT04）。
- [ ] **LT06 Router 失败帧、运行时指标与负载值**（待开发，依赖 LT05）。

## N03 / P1 — 并发容量、排队与取消（ACP-R04、A02）

- 负责人：Codex；状态：待评审（实现与本机/容器验证完成）；初始基线 `origin/dev@768a8c1e`，已改基 `origin/dev@d38a3513`；实现分支 `codex/n03-admission`，PR 待创建。任务输入来自 2026-10-28 非 RAG 拆解，只补充本任务，不覆盖其他会话的规划文件。
- 拥有范围：Application/Execution 的授权后有界准入、Contracts 状态/错误契约、Host SSE 与共享异常映射、前端 chat 等待状态及对应测试。共享边界：Runtime/OpenCodeRuntime 仅保留并收紧最后活跃进程门的等待时限，Dockerfile 仅新增显式 capacity-tests 目标；保活池与默认值由 N04 负责，SDK/架构白名单/RAG 不变，Runner 沿用已合入 dev 的移除结果。
- 对外行为：Host/租户活跃与等待上限、排队/执行分开计时；队列满流前 429 + 重试信息，排队超时 503 或流内 error/done；兼容 execution_status 附加事件。排队取消无准备资源，执行取消在进程/工具/锁回收后归还名额，成功终态仍在保存后发送。
- 验收证据与回退：[N03 运行时说明](../integrations/opencode-runtime.md#n03并发准入排队与取消)。采用 N02 非 root 干净服务身份；生产容量、多租户 OS 隔离、Windows/外部服务商仍不在本机基线结论内。构建、默认测试、前端与真实 1/2/4/8 阶梯结果分别记录，达到资源阈值停止；不提高默认并发，不开启保活。
