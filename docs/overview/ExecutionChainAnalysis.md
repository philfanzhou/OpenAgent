# 执行链路分析（OpenCode 运行时合入后）

分析基线：`dev @ 8799d479`（PR #234 合入后）。性质：现状分析与精简机会盘点，供 [提案评审 A03](../planning/TODO.md) 与 ADR 拆分作输入；本文不改变任何任务状态。协议与配置细节见 [OpenCode 运行时](../integrations/opencode-runtime.md)，分层设计见 [运行时设计](Design.md)。

## 1. 结论摘要

"运行时交给 OpenCode 之后链路不该这么长"这个直觉，对了一半。链路长要拆成四件不同的事，答案各不相同：

1. **进程链路（部署跳数）**：Docker 形态是 浏览器 → nginx → Router → Engine.Host → OpenCode 子进程（Runner 条件性介入）。其中 Router 在单 Engine 部署下退化为纯转发层：它有价值的能力（限流、幂等、查询缓存、多引擎一致性哈希、会话亲和）只在多 Engine / 对外运营场景成立。官方本地预览已经完全绕开它（`scripts/local-preview.py:91-98` 只起 Engine.Host，前端 `engine` 直连模式）。**这是最大的可压缩点，且是部署/定位决策，不是代码问题。**
2. **进程内分层（代码链路）**：MAF 推理栈移除后，Engine.Host → AgentExecutor → RuntimeAgentExecutor → RuntimePreparation → `IAgentRuntimeDriver` → OpenCodeRuntime → Acp SDK → OpenCode 的调用序已经是薄层，每层职责可陈述。剩余的"长"来自四类低成本可收口项：双份认证与租户解析、双份 Agent 选择、纯转发层（facade、双层工厂）、请求对象 3~4 次重塑（见 §4）。
3. **每轮资源链路（per-turn 成本）**：每轮 = 新 OpenCode 子进程 + 随机工作区 + 每轮 loopback MCP 桥（Kestrel）+ 全量历史 JSON 重放 + 会话锁与全量加载/写回。这是链路"重"的真正来源，且是**有意的隔离取舍**（进程不跨轮复用、不依赖 session/load）。压缩它意味着动摇"每轮隔离"承诺，是决策点而非缺陷。
4. **规划中的链路**：[新架构提案](../architecture/新架构方案.html) 与任务清单 A04–A08（Worker、RunGrant、MCP 网关、事件重放、审批、ChatCompat）为"平台自持分布式推理编排"设计（Control 进程 + Worker 容器 + 队列 + 租约 + 快照）。PR #234 实际落地的是"Engine 进程内直接驱动每轮独立 harness"的短链路，规划链路的核心前提（平台需要跨进程编排有状态推理循环）已部分不成立，应在 A03 重新核定范围，而不是默认全部落地（见 §5 D6）。

同时要明确**不可再减**的部分：OpenCode 每轮无状态，平台是会话、租户授权、文件资产与审计的唯一事实源。身份认证、Agent/模型/Runtime 授权解析、会话锁与持久化、工具授权/预算/审计这些横切职责不会因为运行时外移而消失——链路的下界不是"前端直连 OpenCode"，而是"入口 + 身份 + 授权 + 状态 + 持久化 + 一条驱动通道"。

## 2. 现状链路全景

### 2.1 主链（一轮流式聊天）

```text
浏览器（OpenAgent.Chat，默认 router 模式 :5001，可切 engine 直连 :5208）
  → [Router] JwtUserContextMiddleware → 限流/幂等/查询缓存 → Agent 选择 → YARP 转发
  → [Engine.Host] 认证 + AgentUserContextMiddleware + EngineAdmissionMiddleware → /chat/stream
  → [Application] AgentExecutor（纯转发）→ RuntimeAgentExecutor
       ConversationAgentResolver / AgentRuntimeResolver → TurnContext（身份坐标）
       FileAssetRequestResolver（附件）→ RuntimePreparation
         会话锁 + 历史加载（PlatformChatHistory）+ 能力/工具准备 → RuntimeInvocation
  → [Runtime] OpenCodeRuntime：并发租约 → 随机工作区 + Skill 物化 → MCP 桥（每轮 Kestrel）
       → 启动 opencode acp 子进程 → initialize / session/new / session/prompt
  → [OpenCode（不固定版本）] 直接调用模型 API；平台工具经 MCP 桥回调；外部 MCP 直连
  ← session/update → AcpEventMapper → AgentStreamEvent → SSE
  → [持久化] RuntimePreparationScope → PlatformChatHistory → ConversationSessionStore
       → WriteThrough(Redis) → EF Core → PostgreSQL → 落库成功后才发 done
```

### 2.2 工具回调链（OpenCode → 平台）

```text
OpenCode → loopback MCP 桥（随机端口 + 256bit bearer）→ RuntimeTool
  → IsolatedToolFunction（超时/预算/并发/独占门 + 每次调用重查授权）
  → Capabilities 工具实现（文件资产 / RAG / Plan / 用户档案 / Skill 文件）
  → execute_code / run_skill_script / workspace 五件套 / download_file 落盘
  → Runner（bubblewrap 沙箱，独立进程，仅上述入口触达）
```

## 3. 逐跳判定

判定取值：**必要**（横切职责，不可减）／**条件必要**（多引擎或运营场景才成立）／**可收口**（低成本合并/去重）／**空转或失配**（当前不产生价值）／**待决策**（需 ADR）。

| # | 环节 | 存在理由 | 判定 | 证据 |
|---|---|---|---|---|
| 1 | nginx 静态托管 + 反代 | 部署形态 | 必要（部署选择） | `deploy/openagent/docker-compose.yml` |
| 2 | Router 入口认证（JwtUserContext） | 入口身份 | 可收口：与 Engine 侧是两份几乎相同的 Claims→UserContext 实现 | `OpenAgent.Router/Security/JwtUserContextMiddleware.cs:17-58` vs `OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs:44-84` |
| 3 | Router 限流/幂等/查询缓存 | 运营治理 | 条件必要（单机可砍） | `OpenAgent.Router/Program.cs:64-78`、`Middleware/RedisRateLimiter.cs` |
| 4 | Router Agent 选择（意图/会话亲和） | 智能路由 | 空转或失配：与 Engine 侧 `ConversationAgentResolver` 双实现；engine 直连模式下整体被旁路 | `OpenAgent.Router/Endpoints/AgentSelectionFilter.cs:12-76` |
| 5 | Router YARP 转发 + 服务发现/一致性哈希/健康 | 多 Engine 扩展 | 条件必要 | `OpenAgent.Router/Providers/OpenAgentEngineProvider.cs:226-255` |
| 6 | Engine 认证 + 用户上下文 + 准入（3 个中间件） | 身份与入口治理 | 必要；可收口：租户校验在一轮内出现 4 次（用户上下文、准入、Resolver 兜底） | `OpenAgent.Engine.Host/Program.cs:60-73`、`Middleware/EngineAdmissionMiddleware.cs:26-37`、`OpenAgent.Application/Execution/AgentRuntimeResolver.cs:27-31` |
| 7 | AgentExecutor facade | 端点稳定入口 | 可收口：纯转发，单一实现 | `OpenAgent.Application/Execution/AgentExecutor.cs:8-26` |
| 8 | ConversationAgentResolver / AgentRuntimeResolver | Agent 与模型授权快照 | 必要 | `OpenAgent.Application/Conversation/ConversationAgentResolver.cs:9-39`、`Execution/AgentRuntimeResolver.cs:16-80` |
| 9 | FileAssetRequestResolver | 附件绑定与校验 | 必要 | `OpenAgent.Capabilities/Files/Requests/FileAssetRequestResolver.cs:16-54` |
| 10 | PlatformChatHistory（锁/加载/修复/内联图片） | 会话一致性 | 必要；全量重放模式是每轮成本来源（见 §4-e） | `OpenAgent.Application/Conversation/History/PlatformChatHistory.cs:213-259` |
| 11 | RuntimePreparation + IsolatedToolFunction | 工具授权/超时/预算/并发 | 必要（平台横切，OpenCode 不替代） | `OpenAgent.Application/Execution/RuntimePreparation.cs:93-119`、`Tooling/Invocation/IsolatedToolFunction.cs:79-143` |
| 12 | RuntimeToolBridge（每轮 MCP 桥） | ACP 集成下平台工具的回调通道 | 必要；每轮起停 Kestrel 是成本项 | `OpenAgent.Runtime/RuntimeToolBridge.cs:20-116` |
| 13 | OpenCodeRuntime 每轮进程 + 随机工作区 | 隔离（有意取舍） | 必要；进程池化/会话复用属待决策 | `OpenAgent.Runtime/OpenCodeRuntime.cs:81-107,159-173` |
| 14 | AcpEventMapper + usage 采集 | 事件映射 | 必要；但 usage 链路空转：`usage_update` 不映射，TokenUsage 恒 null，落库盖章与 `done.usage` 均为空转 | `OpenAgent.Runtime/AcpEventMapper.cs:82`、`OpenCodeRuntime.cs:152-157` |
| 15 | 持久化链（WriteThrough→EF→PostgreSQL） | 事实源 | 必要 | `OpenAgent.Framework/Conversations/WriteThroughConversationStore.cs:11-173` |
| 16 | Runner（bubblewrap 沙箱） | 脚本/代码执行隔离 | 条件必要（仅 skill 脚本、execute_code、workspace 五件套、download_file 落盘触达） | `OpenAgent.Application/Capabilities/Skill/SkillScriptRunner.cs:95`、`OpenAgent.Capabilities/Code/CodeCapabilitySource.cs:123` |
| 17 | Application 模型 SDK 残留（压缩摘要直连 OpenAI/Anthropic） | 会话压缩摘要 | 待决策：主推理已无 C# 模型 SDK 路径，仅摘要与手动压缩仍直连 | `OpenAgent.Application/ModelProviders/AgentChatClientFactory.cs:117-149`、`Execution/RuntimePreparation.cs:81-88` |
| 18 | 双份端点面（Router 全量转发 vs Engine.Host 全量实现） | 部署拓扑 | 待决策（随 D1 一并定） | `OpenAgent.Router/Extensions/RouterEndpointExtensions.cs:19-172` vs `OpenAgent.Engine.Host/Extensions/EndpointExtensions.cs` |
| 19 | 空占位目录（Worker.Host、Capabilities.Host、Abstractions/Grants/Leases 等） | 规划 | 空转或失配（见 D6） | `Backend/src/OpenAgent.Worker.Host/`（仅 .gitkeep）、`AGENTS.md`「Worker 等空目录仍只代表规划」 |

## 4. 链路"长"的六个来源

a. **入口双轨**：前端连接模式默认 `router`，可切 `engine` 直连（`Frontend/OpenAgent.Chat/src/shared/api/http.ts:25-36`）；Docker 部署经 nginx→Router，本地预览直接 Engine。两套脚本对"谁对外"给出相反答案，每套端点面、认证、Agent 选择都对应维护两份。

b. **安全边界重复**：Claims→用户上下文两份实现（见 §3-2）；租户校验一轮内 4 次（§3-6）。纵深防御可以理解，但重复的是"同一份解析代码在两个进程复制"，不是真正的多因子。

c. **兼容性 facade**：AgentExecutor 纯转发（§3-7）；历史工厂双层跳转（`ConversationHistoryFactory` → `IPlatformChatHistoryFactory` → `PlatformChatHistoryFactory`）；旧命名空间 `OpenAgent.Application.Runtime.Agent` 由架构测试特判保留。

d. **请求对象多次重塑**：`ChatRequest` → `AgentRequest`（`AgentEndpointRequestMapper.cs:11-49`）→ resolved 副本（`RuntimeAgentExecutor.cs:82-95`）→ `TurnContext` + `RuntimeInvocation`。加一个字段要动四处。历史同样双重扁平：工具调用先拍成文本（`RuntimePreparation.cs:146-163`）再 JSON 序列化为嵌入资源（`OpenCodeRuntime.cs:192-224`），结构化 tool-call 语义在边界处丢失。

e. **每轮资源模型**：每轮新建 OpenCode 进程、随机工作区、loopback MCP 桥并全量重放历史（含图片），SDK 的会话恢复能力（`AcpClient.cs` 的 Load/Resume）在主链路完全未用。这是"每轮隔离"的直接代价；并发上限是进程租约（`SemaphoreSlim(8)`），高并发下进程/端口/冷启动开销可观。

f. **规划链路未重审**：提案（`docs/architecture/新架构方案.html`）设计的是 Control 进程（准入、RunGrant、审批、租约、事件入库）+ Worker 容器（领任务、租约、起停 adapter、快照）的三单元长链路；A04–A08 据此排期。实际落地的"Engine 进程内驱动 + 每轮独立进程 + 平台会话事实源"已把 Worker 要解决的问题（隔离、生命周期、回收）用更短的方式解决了一部分，未落地部分需要重新论证而不是排队实现。

## 5. 决策点建议

以下为建议的决策项，供 A03 评审与 ADR 立项；不构成任务分派。

- **D1 入口归一**（最大收益项）。三选一：Router 唯一入口 / 单机以 Engine.Host 为唯一入口（local-preview 已示范）/ 维持双轨但明确各自语义。建议：Engine.Host 是默认形态与唯一业务端点实现；Router 定位收缩为"多 Engine 运营网关"（限流、幂等、缓存、路由表），按需部署；Agent 选择与认证解析单一实现收敛到 Engine 侧，Router 只做治理转发，端点面不再双份维护。
- **D2 纯转发层收口**：合并 AgentExecutor 与 `IAgentExecution`（或端点直接依赖实现）；历史双层工厂合一；请求投影链减一层。均为行为保持式重构，可独立 PR。类/方法级完整清单见 [执行域精简盘点](ExecutionSimplification.md)。
- **D3 usage 链路二选一**：要么映射 `usage_update` 并在 UI/文档明确"上下文占用、非计费 token"；要么删除 TokenUsage 落库盖章与 `done.usage` 的空转路径，前端不再期待该字段。
- **D4 压缩摘要模型直连去留**：保留独立摘要客户端（现状，代价是 Application 维持模型 SDK 依赖）或改走 OpenCode 辅助请求（其 usage 同样不可见）。建议保留并出一条 ADR 记录取舍。
- **D5 每轮进程模型**：维持（推荐，隔离优先）/ 进程池化 / ACP 会话复用。任何变更动摇"每轮隔离"与"无跨轮 harness 状态"两条承诺，必须 ADR 化并附实测冷启动与并发数据（A02 spike 的量化结论可直接复用）。
- **D6 规划任务重审（A03 输入）**：
  - A04 Worker：原始动机（推理编排放隔离进程）已被"Engine 内驱动 + 每轮子进程 + 容器隔离部署"替代，剩余价值仅在"多 Engine 共享执行池/跨机扩容"场景，建议降级为容量问题而非默认架构项；
  - A05 RunGrant/MCP 网关：外部 MCP 已改为 OpenCode 直连透传（凭据由 harness 持有），网关式代理路径已删除；RunGrant 的"短期凭证"语义若保留，范围应重定义为平台工具桥（`openagent_*`）的调用凭证，而非全链路门禁；
  - A06 事件重放/Run 状态机：每轮进程 + 平台会话锁已覆盖一致性；"重放"在无状态 harness 模式下退化为"历史重建"（已实现），建议收敛范围；
  - A07 ChatCompat：现有 SSE 由平台直出，旧接口兼容层的实际消费方需先确认，无人消费则直接关闭；
  - A08 审批：ACP 权限回调当前默认拒绝（`opencode-runtime.md`），审批是功能增项而非链路必需，按产品需求排期即可；
  - 空 `.gitkeep` 占位（Worker.Host、Capabilities.Host、Application 六个空目录、Engine.Host 四个空目录）：随上述结论同步删除或改注释，避免"看起来已规划"的误导。

## 6. 与任务清单的对应

| TODO 项 | 本文影响 |
|---|---|
| A03 提案评审与 ADR 拆分 | 本文为主要输入；§5 六个决策点建议拆 ADR |
| A04 / A05 / A06 / A07 / A08 | 建议在 A03 中按 §5-D6 重新核定范围 |
| A09 切流并移除 MAF | Application 的 MAF 推理栈已随 #234 移除，剩切流验收与包清理 |
| A02 spike | 其冷启动/单机并发量化结论是 D5 的直接输入 |

## 7. 证据附录（关键引用）

- 前端连接模式与双入口：`Frontend/OpenAgent.Chat/src/shared/api/http.ts:25-36`；本地预览绕开 Router：`scripts/local-preview.py:91-98`；Docker 形态：`deploy/openagent/docker-compose.yml`。
- Router 治理与转发：`OpenAgent.Router/Program.cs:64-78`、`Endpoints/AgentForwarder.cs:30-152`、`Providers/OpenAgentEngineProvider.cs:226-255`。
- Engine 入口管线：`OpenAgent.Engine.Host/Program.cs:60-73`、`Extensions/AgentChatEndpointExtensions.cs:15-27`、`Extensions/AgentStreamWriter.cs:12-75`。
- 执行协调：`OpenAgent.Application/Execution/AgentExecutor.cs:8-26`、`RuntimeAgentExecutor.cs:50-178`、`RuntimePreparation.cs:44-131`、`Tooling/Invocation/IsolatedToolFunction.cs:79-143`。
- OpenCode 驱动与桥：`OpenAgent.Runtime/OpenCodeRuntime.cs:21-190`、`RuntimeToolBridge.cs:20-116`、`OpenCodeLaunch.cs:27-37`、`AcpEventMapper.cs:12-84`。
- 持久化：`OpenAgent.Application/Conversation/History/PlatformChatHistory.cs:213-259,498-674`、`OpenAgent.Framework/Conversations/WriteThroughConversationStore.cs:11-173`。
- MAF 移除与兼容范围：`docs/overview/Design.md`「会话一致性与兼容范围」、`docs/integrations/opencode-runtime.md`「兼容组件与后续范围」。
- 规划链路原始设计：`docs/architecture/新架构方案.html`（Control/Worker/RunGrant/审批/事件章节）。
