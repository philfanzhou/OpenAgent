# 任务清单

状态核对：2026-10-08，主干基线 `313bc65dc59e21e94e0f36d4c648d0da9214b882`（[PR #158](https://github.com/philfanzhou/OpenAgent/pull/158) 已合并）。本页是当前待办的唯一入口；模块实际目录见 [开发指南](../overview/DevelopmentGuide.md)，旧方案见 [历史归档](../archive/README.md)。

## 状态与分派规则

- `[x]` 表示已合并并有验收记录；`[ ]` 表示仍有工作。下列未完成任务默认状态为**待开发**、负责人为**未分配**，不能用已有 PR 代替分派或验收。
- 领取任务时在该项补充“负责人 / 状态（待开发、开发中、待评审、待验证、阻塞） / 实现 PR / 验收证据”；有阻塞需记录原因与依赖 ID。
- 每项独立 PR；写明拥有目录、共享文件、契约、验证和回退。公共 DTO、Host Program、项目/包引用、数据库模型/迁移及前端 app 由集成人协调。同一文件的迁移先合入共同基线。
- 后端范围以下以 `Backend/src/OpenAgent.*` 为根，前端以 `Frontend/OpenAgent.Chat/src/` 为根。结构迁移保持协议和行为兼容；功能/schema 变化单独验收及制定回退方案。

## 已完成基线

- [x] **B01 Core 第一批边界**：功能归域、模块 DI、MCP/Skill 扩展接口、Runner 客户端及产物发布复用、测试目录与依赖门禁。证据：[PR #158](https://github.com/philfanzhou/OpenAgent/pull/158)、[验证记录](../test-reports/2026-10-08-module-boundaries.md)。
- [x] **B02 前端第一批边界**：shared transport/wire/SSE、业务 API 归域、模型/MCP/Skill/RAG 设置模块与 import 门禁。证据同 B01。第一批边界不代表下列功能已完成。

## P0：异常追溯与会话恢复

具体可认领功能见 [功能 Issue 索引](functional-issues.json)，首批共 **68 条、654 人时**；下列 F01～F07 是方向概览，不与子任务重复计数。后续任务目标、工时、状态和依赖以 GitHub Issue 为准。

统一 [Issue 格式和动态报表流程](issue-management.md)已写入仓库；Excel 在汇报时生成，甘特图按实时 Issue 状态与原生依赖重新排期。当前 [容量配置](project-schedule.json)为 1 名开发、每周 5 个有效人日，不预先指定认领人。

- [ ] **F01 完整异常记录与会话重放**（待分派）。主责：Core/Observability、Execution、Tooling/Invocation；交界：Contracts 错误契约、Infrastructure 持久化、Host SSE、前端 diagnostics。
  验收：执行、模型、MCP/Skill/Runner、压缩、历史读写及资源清理异常均有完整堆栈、内部异常、调用阶段与 Tenant/User/Conversation/Trace/Agent 关联；原始输入输出有可追溯记录，超长内容不静默丢失；凭证按规则脱敏；能依据记录重建调用过程，取消和清理失败不覆盖原始错误。

- [ ] **F02 超长上下文与压缩恢复**（待分派）。主责：Core/Conversation/{History,Compaction}、Tooling/Invocation、ModelProviders；协调 S01、I01、I02、I04，预算保护不必等待全部目录迁移。
  验收：在调用摘要模型前校验模型预算，对超大 MCP/SDK Skill 工具结果、单条输入及最新消息组采取有界处理；保留原始记录与引用；摘要调用失败有确定性降级，下一轮和手动恢复可用；测试自动/手动压缩、溢出、失败、取消、并发与重启续接。参考 [压缩文档](../modules/conversation/context-compression/README.md)。

## P1：身份与统一权限

- [ ] **F03 MCP 可靠认证**（待分派）。主责：Core/Capabilities/Mcp、Hosting、Engine.Host；前置：明确 Agent→MCP→数据服务的身份、信任边界与凭证管理契约。
  验收：服务端验证调用身份、凭证可轮换和撤销、第三方未认证调用拒绝、跨租户凭证不复用、认证失败可审计；Engine 登录不能代替远端 MCP 认证。关联 [Issue #147](https://github.com/philfanzhou/OpenAgent/issues/147)。

- [ ] **F04 MCP/Skill/Agent 统一授权**（待分派）。主责：Core/Security、Contracts；交界：各能力、Engine 配置目录、Router 与 Hosting。与 F03 共定主体、资源、动作及租户范围。
  验收：能力发现和实际调用均检查权限；覆盖 Agent、模型、MCP、Skill 与工具资源的越权、跨租户、权限撤销与默认策略；目录可见性与执行权限一致，审计可追溯。参考 [授权文档](../modules/security/permission/README.md)。

## P2：生产保障与编排

- [ ] **F05 生产容灾备份**（待分派，生产上线前必须验收）。主责：数据库与部署运维；交界：Infrastructure、对象存储、配置密钥。
  验收：先确定 RPO/RTO、保留周期和恢复责任人；自动备份 PostgreSQL，并明确 S3 文件与 Data Protection 密钥的配套恢复；具备失败告警、恢复脚本和实际恢复演练记录，校验会话、文件引用和历史凭证解密。

- [ ] **F06 Workflow 多 Agent 编排**（待分派）。主责：编排与 Router；交界：Execution、Contracts、Security。先定工作流输入输出、状态、取消和持久化契约。
  验收：多 Agent 复杂流程可跟踪与恢复，发现、鉴权、健康检查、超时和降级完整；不得将现有 `workflow` 路由占位当成已部署服务。关联 [Issue #150](https://github.com/philfanzhou/OpenAgent/issues/150)。

- [ ] **F07 风险操作审批**（待分派）。主责：Execution、Security；交界：Host SSE、前端 chat、会话持久化。评估 MAF 原生审批扩展，协调 F04/F06。
  验收：批准、拒绝、超时、取消与重启续接都有明确状态；批准前不执行风险操作，恢复不能绕过审批或重复执行。关联 [Issue #149](https://github.com/philfanzhou/OpenAgent/issues/149)。

## 剩余模块拆分

原 T00～T05、T07～T10 的第一批工作见 B01/B02；下列保留原任务编号映射，目标目录不表示已经存在。

- [ ] **S01 / T06** — Core/Conversation/History：PlatformChatHistory 拆为修复、流式缓冲、锁续租、图片历史加载职责
  依赖与验收：协调 I01/I04；工具配对、partial/取消、锁释放/续租、历史与压缩保真
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
- [ ] **S11 / T18** — Runner/Program 拆 Endpoints；BubblewrapCodeExecutor/Process、SessionSandbox/Manager 归 Sandbox；WorkspaceStore/Reaper 归 Workspace
  依赖与验收：协调 I06；小写 sandbox 部署资源保留，安全参数/路径/并发/清理不变；Linux 门控与服务 smoke 通过
- [ ] **S12 / T19** — 剩余域依赖检查、公开面、注册与全链验收收口
  依赖与验收：各拆分完成后汇总；真实模型/Runner、Linux 沙箱及执行、文件、授权、路由交界验证；macOS 跳过不算 Linux 验收

拆独立程序集前须有独立发布、复用或依赖隔离需求；验证无循环、内部依赖、独立测试和公共 API，并同步 ADR、AGENTS、编码规则与架构测试。关联 [Issue #148](https://github.com/philfanzhou/OpenAgent/issues/148)、[Issue #151](https://github.com/philfanzhou/OpenAgent/issues/151)。

## 已有 PR 的集成待办

以下 PR 在核对日均为 OPEN。先适配 #158 合并后的目录和接口，再按目标验收；PR 合并后更新本清单与对应功能任务，不能沿用历史方案的完成状态。

- [ ] **I01** — [#146 自动/手动压缩](https://github.com/philfanzhou/OpenAgent/pull/146)、[#144 会话续接](https://github.com/philfanzhou/OpenAgent/pull/144)
  依赖与验收：Conversation/History、Compaction；对照 F02 与 S01，统一持久化续接和压缩状态
- [ ] **I02** — [#152 MAF 工具策略](https://github.com/philfanzhou/OpenAgent/pull/152)
  依赖与验收：Tooling/Invocation 与 Skill；动态 SDK 工具同样受预算、超时、并发、授权与异常策略约束
- [ ] **I03** — [#153 执行准备与流式适配](https://github.com/philfanzhou/OpenAgent/pull/153)
  依赖与验收：Execution 与 Host Streaming；单次准备、释放、取消、SSE 映射兼容
- [ ] **I04** — [#154 历史修复与压缩工厂](https://github.com/philfanzhou/OpenAgent/pull/154)
  依赖与验收：Conversation/History、Compaction；与 I01/S01 共用契约与验收，避免重复拆分
- [ ] **I05** — [#155 DI 幂等与参数读取](https://github.com/philfanzhou/OpenAgent/pull/155)
  依赖与验收：各域注册入口；#158 已包含部分幂等工作，先比较剩余差异，再验证重复绑定与配置读取
- [ ] **I06** — [#156 Runner 适配](https://github.com/philfanzhou/OpenAgent/pull/156)
  依赖与验收：Integrations/Runner、Files/Artifacts；先去除 #158 已覆盖部分，验证 Code/Skill/Workspace 与失败清理
- [ ] **I07** — [#157 RAG Provider 验证](https://github.com/philfanzhou/OpenAgent/pull/157)
  依赖与验收：Capabilities/Rag；遵守兼容门禁，保留检索与降级契约，不先切换未经验证的生产路径

## 建议执行顺序

1. 分派 F01/F02，先补故障记录与超长输入回归；同步整合 I01/I02/I04，其余已有 PR 先核对重叠范围。
2. 结构任务按拥有目录独立推进，S02→S03 串行；S01 与压缩共用基线，前端 S08～S10 协调共享文件。
3. F03/F04 先统一身份和权限契约，再分派各能力接入；生产上线前完成 F05，随后推进 F06/F07。
