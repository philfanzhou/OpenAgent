# 执行域精简盘点（AgentExecutor / MCP / Skill / Contracts）

分析基线：`dev @ 8799d479`。性质：类/方法/字段级的精简清单，是 [执行链路分析](ExecutionChainAnalysis.md) 决策点 D2 的展开；不改变任何任务状态。所有判定附证据（文件:行号，路径省略 `Backend/src/`）。

> **落地状态（2026-10-21）**：§3 的 AgentExecutor/IAgentExecution/双层工厂/ToolInvocationScope/死字段复制、§4 的 MCP 客户端测试器组 + Redis 双写层 + 内存注册表、§5 的 SkillCatalog/RedisSkillCatalogStore 塌缩 + SkillFeatureFactory + test 端点、§6 的 7 个死类型与空目录、§3/§8 的预算与指标修正已随精简 PR 落地。§7 压缩影子链与 §6 的大宗 DTO 合并（身份 5→1、Chat 双契约线、连接测试/模型形状/文件分享合并、接口下沉）仍待决策或后续 PR。

## 1. 结论摘要

- **AgentExecutor 确实多余**：它是纯转发 facade，与 `IAgentExecution` 一起可以整层删掉（端点直接依赖 `RuntimeAgentExecutor`）。
- **MCP 与 Skill 的每轮主路径已经接近最小**：MCP 透传只剩"注册表读取 → 租户过滤/授权 → 映射成 `RuntimeMcpServer(name,url,type)`"，Skill 只剩"物化 + 校验 + 两个桥工具 + 沙箱脚本"。多余的是**管理面与缓存层残留**：平台侧最后一套 MCP 客户端（连接测试器）、MCP 的 Redis 双写层、死掉的内存 SkillCatalog、冗余的 test/validate 端点。
- **Contracts 有死代码与大量合并空间**：7 个零引用类型、3 个空目录；同一身份形状存在 5 份、连接测试/模型连接/文件分享等 DTO 各有 3~4 份重复。
- **"够用的运行配置"已经存在且边界清晰**：`RuntimeInvocation` 七个字段全部被 OpenCode 驱动消费，无一冗余（§2）。冗余不在主链路形状，在外围。
- **最大的隐藏块是"压缩影子链"**：推理交给 OpenCode 后，Compaction → ModelProviders（OpenAI/Anthropic SDK 直连）→ LLM 交互审计写路径这条链的唯一服务对象是"压缩摘要调用"（自动压缩默认关闭，活的是手动压缩按钮）。它的去留一次性决定约 1000 行代码与 Application 对模型 SDK 的最后依赖（§7）。

## 2. "够用的运行配置"边界

`RuntimeInvocation`（`OpenAgent.Contracts/Runtime/IAgentRuntimeDriver.cs:14-21`）字段级消费核对：

| 字段 | 驱动消费点 | 判定 |
|---|---|---|
| Profile | `OpenCodeRuntime.cs:27,34,102,106`；`OpenCodeLaunch.cs:15,54` | 保留 |
| Turn | `OpenCodeRuntime.cs:83,156`（工作区分区哈希、Usage 事件） | 保留 |
| Instructions | `OpenCodeLaunch.cs:53`（agent prompt） | 保留 |
| Messages | `OpenCodeRuntime.cs:192-223`（历史 JSON 资源 + 当轮文本/图片） | 保留 |
| Tools | `RuntimeToolBridge.cs:28,43-61`（四字段全消费） | 保留 |
| Skills | `OpenCodeSkills.cs:12-31`、`OpenCodeLaunch.cs:28-37` | 保留 |
| McpServers | `OpenCodeSessionMcp.cs:9-21`、`OpenCodeLaunch.cs:28` | 保留 |

`TurnContext` 七字段中 `AgentId`/`ConversationType` 驱动不读，但平台横切（会话存储、工具授权、文件 scope）在用，保留。**Runtime 契约本身无可删字段**；真正的死字段在入口请求对象上：`RuntimeAgentExecutor.cs:92-94` 复制的 `ExternalContext`、`ClientType`、`IdempotencyKey` 下游零读取，可删（`ExternalContext`/`ClientType` 若 Router 侧确认无消费可进一步从 `AgentRequest` 移除）。

## 3. Application 执行栈

| 动作 | 对象 | 证据 |
|---|---|---|
| **删** | `AgentExecutor` + `IAgentExecution` 整层（端点直接依赖 `RuntimeAgentExecutor`） | `Execution/AgentExecutor.cs:17-25` 两方法各一行转发；`IAgentExecution` 唯一实现、唯一消费者即 facade |
| **删** | `AgentCompositionLog`（零调用方） | 全仓仅自身定义 `Execution/AgentCompositionLog.cs:5` |
| **删** | `RuntimeAgentExecutor.cs:92-94` 三行死字段复制 | 见 §2 |
| **删** | 7 个 `.gitkeep` 空目录（Abstractions/Admission/Events/Grants/Leases/Runs/Sessions） | 目录内无代码 |
| **合并** | `ConversationAgentResolver`（41 行，单调用方）并入 `RuntimeAgentExecutor` | `Conversation/ConversationAgentResolver.cs:9-39` |
| **合并** | `IPlatformChatHistoryFactory` + `PlatformChatHistoryFactory` 双层工厂并入 `ConversationHistoryFactory`（删接口+实现+两条 DI 注册） | `PlatformChatHistoryFactory.cs:8-9`、`ConversationServiceExtensions.cs:24-26` |
| **合并** | `ToolInvocationScope`（13 行，仅一个 `SemaphoreSlim`）并入 `RuntimePreparationScope` | `Tooling/Invocation/ToolInvocationScope.cs:6` |
| **合并（可选）** | `RuntimePreparation` 并入 `RuntimeAgentExecutor`：preparation 侧只剩约 90 行一次性组装，唯一调用方是 executor | `RuntimePreparation.cs:44-131` |
| **修正** | `ToolResultBudgets`：删已不存在工具的 `load_skill`/`read_skill_resource`（:40-41）与不可达的 `mcp__` 分支（:53-56，平台已无 mcp__ 工具名生产者）及 `AgentExecutionOptions.McpToolResultCharBudget`；补 `read_skill_file` 进 ReadTools（现落默认 60k 而非读类 80k） | `Tooling/Invocation/ToolResultBudgets.cs` |
| **修正** | `EngineMeter.IsSkillCall` 仍列已删工具名；`IsolatedToolFunction` 类注释仍写 MAF 时代的 `FunctionInvokingChatClient` | `Observability/EngineMeter.cs:72-73`、`Tooling/Invocation/IsolatedToolFunction.cs:12-13` |
| **保留** | `RuntimeAgentExecutor`、`RuntimePreparationScope`、`AgentRuntimeResolver`（接口被压缩服务共用）、`PlatformChatHistory` 主体（锁/加载/部分落库/终态语义均必要）、`CapabilityToolFactory`、`IsolatedToolFunction` 本体 | 链路分析 §3-8/10/11 |

`PlatformChatHistory`（745 行）无整块可删：`RepairToolHistory`（:404-495，约 90 行）的唯一结构需求来自**压缩摘要请求**的 tool_calls 严格配对（OpenCode 主路径已把工具历史扁平成文本），随压缩决策进退（§7）；`DisposeAsync` 与 `PersistFailureAsync` 的 Cancelled 分支重复约 20 行可抽公共方法。

## 4. MCP 链

**最小透传路径（现状已接近最小）**：`RuntimePreparation` → `McpFeatureFactory`（`EnabledServerIds` → `McpConfigRepository`（PG）→ 租户过滤 + 授权 → `RuntimeMcpServer(name,url,type)`）→ 汇入 `RuntimeInvocation.McpServers` → `OpenCodeSessionMcp.Map` + 平台桥合并 → `session/new` → OpenCode 原生连接 + 权限放行（`McpFeatureFactory.cs:16-69`、`OpenCodeSessionMcp.cs:9-22`、`OpenCodeLaunch.cs:27-32`）。

| 动作 | 对象 | 证据 |
|---|---|---|
| **删** | 平台侧最后一套 MCP 客户端：`McpConnectionTester`（121 行）+ `McpTransportFactory`（55）+ `McpClientOptionsFactory`（21）+ `McpExecutionOptions`（7）+ `test-connection` 端点（前端同步处理）。测试行为已不代表运行链路（透传后连接由 OpenCode 持有），可降级为 HTTP 可达性检查或直接删除 | `Application/Capabilities/Mcp/McpConnectionTester.cs:10-121`；端点 `Engine.Host/Extensions/ConfigurationEndpointExtensions.cs:35-56`；前端 `features/mcp/api.ts:5-23` |
| **删** | MCP 的 Redis 层：`RedisMcpRegistrar` + `RedisRegistrarBase` 的 MCP 部分 + `McpProfileManagementService` 的 Redis 事务/pub-sub/index 分支（约 120 行，保留 repository 分支约 40 行）+ `McpRegistry` 内存兜底（PG 命中时永不读取） | `Engine/Config/McpProfileManagementService.cs:97-152`、`Engine/Redis/RedisMcpRegistrar.cs`、`Application/Capabilities/Mcp/McpRegistry.cs:6-22` |
| **删（迁移后）** | 内联 `McpConfig.Servers` + `McpServersConverter` 兼容载荷 | `Contracts/Configuration/AgentConfig.cs:97-158` |
| **保留** | `McpFeatureFactory`（唯一透传核心）、`McpConfigRepository`（数据源）、CRUD 管理端点（Development-only，注册表是产品功能） | — |

附带观察：每轮每启用 server 一次 PG 查询（`McpFeatureFactory.cs:34-44`）；如未来需要缓存，正确位置是 repository 侧投影，而不是恢复 Redis 双写。

## 5. Skill 链

**最小物化路径**：factory → `ISkillCatalog.GetAsync`（PG）+ 内嵌实例兜底 + 逐 skill 授权 → 对象存储读 index → SHA-256 逐文件校验 + 租户分区断言 → 写临时目录 → `RuntimeSkill` + `read_skill_file`/`run_skill_script` 桥工具 → `OpenCodeSkills.Materialize` symlink 进工作区 → 原生 skill 工具发现（`AgentSkillsProviderFactory.cs:24-352`、`OpenCodeSkills.cs:10-59`）。物化/完整性/路径逃逸防护/Runner 沙箱脚本是不可减的安全与产品逻辑。

| 动作 | 对象 | 证据 |
|---|---|---|
| **删** | `SkillCatalog` 内存字典：默认注册被 Engine 必然 `Replace`，`Register` 全仓无调用方——死代码 | `Capabilities/Skills/SkillCatalog.cs:6-48` |
| **合并** | `RedisSkillCatalogStore`（212 行）塌缩：PG 模式下每次直读 PG、本地字典只写不读、Redis 键仅 repository 为 null 时用。方向：`EfCoreSkillDefinitionRepository` 直接实现 `ISkillCatalog`，删缓存层 | `Engine/Redis/RedisSkillCatalogStore.cs:16-212` |
| **合并** | `SkillFeatureFactory`（25 行纯转发）并入 `AgentSkillsProviderFactory`，feature factory 少一层 | `Application/Capabilities/Skill/SkillFeatureFactory.cs:8-25` |
| **删** | skill `test` 端点 + `ValidateAsync`（每包全量重读+哈希校验）：每轮物化已做同样校验，纯冗余 | `Engine.Host/Skills/SkillPackageManagementService.cs:496-557`、`ManagementEndpointExtensions.cs:60` |
| **删（迁移后）** | legacy ZIP `PackageFormat` 分支（约 20 行） | `AgentSkillsProviderFactory.cs:265-281` |
| **保留瘦身** | `SkillPackageManagementService`（709 行）：上传/对象存储写入是数据源职责；`UpdateMarkdown` 可从整包重存改为只写 SKILL.md 对象 | `Engine.Host/Skills/SkillPackageManagementService.cs:260-341` |
| **保留** | `SkillScriptRunner`（Runner 沙箱执行，三重开关 + 四重授权）、`AgentSkillPackageArchive`、`AgentSkillsRuntime`；**Runner 整体维持**（execute_code、run_skill_script、workspace 五件套、download_file 落盘四个产品功能共享唯一沙箱） | `SkillScriptRunner.cs:22-316` |

## 6. Contracts

依赖纯净性达标：csproj 零包引用，无 `Microsoft.Extensions.AI`/MAF 残留（`Contracts.csproj`）。问题在死类型与重复形状。

**死类型（src 与测试引用均为 0，已复核）**：

| 类型 | 位置 | 说明 |
|---|---|---|
| `IAgentMatrixApi` | `Services/IAgentMatrixApi.cs` | 已删后台客户端接口，`Contracts.Services` 命名空间全库无人 using；连带删目录 |
| `M2MAuthRule` | `Security/M2MAuthRule.cs` | 仅被上一条引用 |
| `DictionaryHelper` | `Helpers/DictionaryHelper.cs` | `TryGetValueIgnoreCase` 零调用；连带删目录 |
| `Citation` + `AgentResponse.Citations` | `Requests/AgentResponse.cs:6` | MAF/RAG 时代遗留，从未赋值 |
| `ToolCallLog` + `AgentResponse.ToolCalls` | `Requests/AgentResponse.cs:7,23` | 工具明细已由 `AgentStreamEvent` 承载 |
| `ToolExecutionException` | `Security/Exceptions.cs:18` | 工具错误已改为 in-band `ToolResult.Error` 信封 |
| `AudiencePermissionDeniedException` | `Security/Exceptions.cs:44` | 无构造点（错误码 6001 由 `AgentException` 直接使用） |

空目录 3 个：`Audit/`、`Grants/`、`Runtime/WorkerProtocol/`（已删 worker 协议残留）。

**合并候选（按收益排序）**：

1. **身份形状 5 → 1**：`IAgentUserContext`/`AgentUserContext`/`ThirdPartyApiKeyIdentity`/`ICurrentUserContext`/`MeResponse` 是同一身份形状（UserId/Username/TenantId/Roles/…）的五份表达。收敛到 `IAgentUserContext` 一份，`MeResponse` 保留为单点投影。
2. **Chat 线双份**：`ChatRequest`/`ChatResponse` 与 `AgentRequest`/`AgentResponse` 并存，靠 `AgentEndpointRequestMapper` 纯字段拷贝桥接；Router 侧还有"反序列化再原样转发"的双重转换。方向：端点直接收 `AgentRequest`，删 `ChatContracts.cs` 与 mapper（与链路分析 D1 入口归一联动）。
3. **连接测试 3 → 1**：`Llm/Rag/Mcp ConnectionTestResult` 三份同构 `{Success, LatencyMs, Error, TraceId}`，合并为一个通用结果。
4. **模型连接形状 3 → 1 基形**：`LlmConfig`/`LlmProviderProfile`/`OpenCodeModelConfig`（ModelId/Endpoint/ApiKey/Format/…）提取共享基形。
5. **文件分享 4 → 2**：`FileShareLink`≈`FileShareSummary`（只差一个 Url）、`FileShareLinkRecord`/`FileShareLinkResponse` 合并文件；`FileAssetResponse` 与 `FileAsset` 11 字段全同，删纯拷贝 mapper。
6. **读写分离**：`IConversationQueryService` 的 4 个方法与 `IConversationStore` 同名方法签名完全重复——从 Store 删掉只读方法，消除双份。
7. **接口下沉（非跨项目契约不该放 Contracts）**：`IConversationLock(+Handle)`（实现与消费都在 Application）→ Application；`IConsistentHashRing`（仅 Router）→ Router。
8. **同一实现双接口**：`IAgentConfigProvider` + `ILlmConfigProvider` 由 Engine 同一个 `ConfigurationService` 承载，合一。
9. **语义重叠**：`IMcpRegistry`（内存）与 `IMcpConfigRepository`（持久）操作同一 `McpServerConfig`，注册表应改为仓储的缓存投影（随 §4 Redis 层删除一并消失）；`IRagRegistry` 同模式。
10. **极小类型/方法折叠**：`FileObjectScope`→并入 `FileObjectWriteRequest.cs`；`FileAssetEnums`→`FileAsset.cs`；`FileObjectReference`（单属性包装）考虑改 string 或并入相邻文件；`CodeExecutionConfig`（单 bool）→`AgentConfig.cs`（注意 Framework 列反序列化兼容）；`SummarizeOptions`（2 字段）内联进 `ContextPolicy`；`PasswordLoginRequest`/`TokenResponse`→统一认证契约文件；`SkillTypes`/`RagAdapterType` 常量类并入各自 Config；`AppendResult.Ok` 两参重载无调用可删；`SkillUploadResponse`/`SkillInstallResponse` 合并。

## 7. 结构性决策：压缩影子链

推理交给 OpenCode 后，平台仍有一条进程内模型调用链：**Compaction（摘要）→ `AgentChatClientFactory`（OpenAI/Anthropic SDK 直连）→ `LlmInteractionRecorder`（审计写库）**。

- 现状：自动压缩由 `ConversationStoreOptions.EnableAutoCompaction` 控制，**默认关闭**且部署未开启；活跃入口只有前端"手动压缩"按钮（`App.vue:304` → compact 端点）。
- 依赖闭包（约 1000 行）：`Conversation/Compaction/`（755 行，含 `AuditedCompactionStrategy` 349 行）、`ModelProviders/` 全目录（`AgentChatClientFactory` 等生产调用方只剩两个摘要入口）、`Observability/` 写路径（`Source.AgentTurn` 成为死枚举值；读侧面板现在只展示摘要调用）、`PlatformChatHistory.RepairToolHistory`（唯一结构需求来自摘要请求的 tool_calls 配对）。
- 二选一：**(a) 承认现状**——保留手动压缩，把 `Source.AgentTurn`、流式分支等死路径收窄，出一条 ADR 记录"Application 保留模型 SDK 仅为摘要"；**(b) 整体退役**——手动压缩入口下线（或改由前端/OpenCode 承担），连带删除 Compaction + ModelProviders + LLM 审计写路径 + RepairToolHistory，Application 彻底不依赖模型 SDK。选项 b 是大动作（牵动前端面板、ContextSummary 持久化与 5 个测试文件），必须产品侧确认。

## 8. 汇总与建议顺序

| 类别 | 量级（行数按上述证据累加，约数） | 内容 |
|---|---|---|
| 直接删除（零风险） | ~150 | Contracts 7 死类型、3 空目录、Application 7 空目录、`AgentCompositionLog`、死字段复制、`SkillCatalog`、`AppendResult.Ok` 重载 |
| 删除（低风险，行为保持） | ~520 | `AgentExecutor`+`IAgentExecution` 层、MCP 客户端测试器组、MCP Redis 双写层、skill test/Validate、legacy 兼容分支 |
| 合并（行为保持重构） | ~350 | 双层历史工厂、`ConversationAgentResolver`、`ToolInvocationScope`、`SkillFeatureFactory`、`RedisSkillCatalogStore` 塌缩、身份 5→1、连接测试 3→1、模型形状基形、文件分享/文件资产 DTO、读写分离、接口下沉 |
| 决策依赖 | ~1000 | 压缩影子链（§7）三选一后的连带范围 |

建议按独立 PR 推进：① 死代码与空目录；② AgentExecutor 层删除 + 执行栈小合并；③ MCP 残余清理（含前端 testMcp）；④ Skill catalog 塌缩；⑤ Contracts 合并（可再拆身份/DTO/接口下沉）；⑥（决策后）压缩影子链处置。**不变量**：SSE 事件契约、数据库 schema、授权链（发现+调用双查）、Runner 与沙箱策略均不动；`Architecture.Tests` 随删除同步收紧白名单而非扩大。

## 9. 证据附录

- 死类型复核：`grep -r IAgentMatrixApi|M2MAuthRule|DictionaryHelper|TryGetValueIgnoreCase|ToolExecutionException|AudiencePermissionDeniedException` 全仓仅命中自身定义；`Citations`/`ToolCalls` 仅在 `AgentResponse.cs:6-7,23` 声明。
- Runtime 契约消费：`OpenAgent.Runtime/OpenCodeRuntime.cs`、`RuntimeToolBridge.cs`、`OpenCodeLaunch.cs`、`OpenCodeSkills.cs`、`OpenCodeSessionMcp.cs` 全量字段核对。
- MCP/Skill 主路径：`OpenAgent.Application/Capabilities/Mcp/McpFeatureFactory.cs`、`Capabilities/Skill/AgentSkillsProviderFactory.cs`、`OpenAgent.Framework/Configuration/McpConfigRepository.cs`、`Framework/Skills/EfCoreSkillDefinitionRepository.cs`。
- 压缩链调用方：`RuntimePreparation.cs:81-91`、`Conversation/Compaction/ConversationCompactionService.cs:74`、`ModelProviders/AgentChatClientFactory.cs:66-69,101-115`、`Engine.Host/Extensions/ConversationEndpointExtensions.cs:125-158`。
