# 模块拆分后续任务

基线：2026-10-08，`9ba17fbf28839696767044dfa8238b85a1690089`。当前目录、边界与分工规则统一见 [开发指南](../overview/DevelopmentGuide.md)，本批验证见 [测试记录](../test-reports/2026-10-08-module-boundaries.md)。下表是待实施计划，不表示目标目录已经存在。

## 当前范围

第一批已完成 Core 功能归域、模块 DI、MCP/Skill 接口接入、Runner 客户端与产物发布复用、相关测试目录迁移；前端已完成 shared transport/wire/SSE、业务 API 归域及模型/MCP/Skill/RAG 设置模块。

原任务 T00～T05、T07～T10 的第一批边界已落地；T06 历史内部职责、T15 类型生成/UI 类型、T16 Agent 设置、T17 旧前端组件与状态、T19 全链验收仍有剩余。本次不新增部署服务、独立数据库或程序集，不包含 P0～P2 的全部功能实现。

## 剩余结构任务

路径以 Backend/src/ 下对应 OpenAgent.* 项目为根；前端路径以 Frontend/OpenAgent.Chat/src/ 为根。每个任务独立 PR，移动文件、提取职责和行为改变分别验收，阶段门禁随任务落地。

| ID | 当前入口 → 目标职责 | 前置与验收 |
|---|---|---|
| T06 | Core/Conversation/History/PlatformChatHistory → 历史修复、流式缓冲、锁续租、图片历史加载 | 在本批边界上推进；复用既有会话工作，验证工具配对、取消 partial、锁释放/续租、历史与压缩保真 |
| T11 | Engine.Host/Extensions 下配置、管理、Chat、Conversation、FileAsset、FileShare 端点 → Endpoints 对应域与 Admin/{Agents,Models,Mcp,Rag,Skills}；writer/headers/heartbeat/payload → Streaming | 保留原 Map* 汇总、认证、Development-only 管理映射、HTTP/SSE 顺序与 done |
| T12 | Engine.Host/Skills/SkillPackageManagementService → Engine/Management/Skills；Host 留 HTTP 映射 | 依赖 T11；Contracts 新增纯领域接口/命令/结果，Engine internal 实现；验证上传更新删除、绑定、版本冲突及失败清理 |
| T13 | Engine.Host/Files 中 S3FileObjectStore、S3Presigner、存储 Options/Validator → Infrastructure/ObjectStorage；存储健康检查 → Host/Health | 可与 T11/T14 并行；AWSSDK.S3 依赖同行迁移，Host 注册门面保留且不双注册；上传下载、签名、校验/readiness 一致 |
| T14 | Infrastructure/Persistence/OpenAgentDbContext.OnModelCreating → Persistence/Mapping 的每实体 IEntityTypeConfiguration | 可独立推进；比较设计时模型与现有快照，关系/index/schema 不变，不生成 schema migration |
| T15/T16 | shared wire 类型 → 核对既有 codegen；UI 类型归本域；app 内 Agent 编辑 → features/agents（确需独立开发时） | 保留 JSON、兼容导出、CRUD/草稿/选中 Agent 与切页状态；不重复已有类型生成工作 |
| T17 | useChatStreaming/useConversationStreams、ChatMessages/MessageComposer → chat；useConversationState/conversationCollection/ChatSidebar → conversations | API 边界已就绪；会话选择由 app 注入，验证串流、取消、刷新重建及列表状态 |
| T17 | useAuthentication/auth/LoginPage → auth；useFileHandling/markdownAssets → files；HealthCheckPanel/InteractionDebugDialog/interactionPresentation/healthCheckCache → diagnostics | shared transport 不反向引用 auth；Markdown 注入文件解析器；验证登录、上传授权、健康检查与追溯 |
| T17 | markdown/typewriterQueue/streamingAssistantContent 通用部分 → shared/markdown 或 streaming；usePanelLayout → app | 不含 feature 业务状态；与前两项协调同一文件迁移，入口只组合工作台 |
| T18 | Runner/Program → Endpoints；BubblewrapCodeExecutor/Process、SessionSandbox/Manager → Sandbox；WorkspaceStore/Reaper → Workspace | 客户端边界已就绪；小写 sandbox 部署资源保留，安全参数/路径/并发/清理不变；Linux 门控与服务 smoke 通过 |
| T19 | 剩余域的依赖检查、公开面、注册与全链验收收口 | 在以上任务完成后汇总；包含 Linux 沙箱、真实 Runner/模型，以及执行、文件、授权、路由交界验证 |

涉及同一文件的迁移先集成共同基线再继续实现；Contracts、Host Program、DbContext/快照/迁移、项目/包引用及前端 app 的变更由集成人协调。结构迁移不改变数据协议，回退对应 PR 即可；功能和 schema 改变另列回退方案。

## 功能任务分派

| 优先级 | 工作包 | 主责与交界 |
|---|---|---|
| P0 | 异常捕获、完整记录与重放 | Observability、Execution、Tooling/Invocation；协调错误契约、持久化、Host SSE 与诊断 UI，凭证不进入排障记录 |
| P0 | 超长上下文与压缩 | Conversation/History、Compaction、Tooling/Invocation、ModelProviders；验证单条超大输入/工具结果、模型预算、完整记录与失败后的会话恢复 |
| P1 | MCP 认证 | Capabilities/Mcp、Hosting、Host；明确 Agent 身份传递、凭证管理与远端 MCP 验证 |
| P1 | 统一授权 | Core/Security 与各域能力入口；协调 Contracts 权限模型、租户范围及 Engine/Router 可见性 |
| P2 | 生产数据库容灾 | 数据库部署与运维；明确恢复目标，验收备份、恢复演练 |
| P2 | Workflow 与风险审批 | 在编排/审批实现前先定与 Execution、Router、Security 的契约；评估 MAF 原生扩展 |

## 既有工作集成

会话拆分与类型生成先核对 [既有会话规划](./2026-09-session-architecture.md) 和实际分支。旧分支的 PR #146/#154 适配 Conversation/History、Compaction 并统一验收；#152 工具策略适配 Tooling/Invocation 与 Skill；#153 适配 Execution；#155 在各域注册入口复验重复绑定；#156 适配 Integrations/Runner；#157 遵守 RAG 域门禁。状态以远端为准，不复制未验收功能。

进一步拆独立程序集前，必须有独立发布/复用/依赖隔离需求，并验证无循环、内部依赖、可独立测试和公共 API；同步 ADR、AGENTS、编码规则与架构测试，不为每种能力新建项目。
