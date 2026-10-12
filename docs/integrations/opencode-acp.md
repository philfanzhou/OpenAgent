# OpenCode ACP 执行后端可行性调研

初次调研：2026-10-09。重新评估：2026-10-12。状态：技术可行性评估，尚未选型、实现或完成运行验证。

**本次重新评估的结论与量化范围见 [第 9–13 节](#9-重新评估替换量与统计口径)。** OpenCode 能接管的范围包括执行循环、模型适配、执行历史、压缩和部分能力运行机制。保留全部现有产品功能时，需要以平台 MCP 服务、OpenCode 插件、模型网关和 worker 管理补齐；不能把受影响代码量当作净删除量。

仓库基线：`313bc65dc59e21e94e0f36d4c648d0da9214b882`，2026-10-12 复核未变。第 1–8 节保留初次调研的发布源码基线：[OpenCode v1.18.35](https://github.com/anomalyco/opencode/releases/tag/v1.18.35)，发布于 2026-10-06 UTC。初次调研时本机未发现 `opencode` 可执行文件；两次评估均未安装软件或发起真实模型请求。第 9–13 节重新核对 GitHub latest release 返回的 [v1.19.0](https://github.com/anomalyco/opencode/releases/tag/v1.19.0)，发布于 2026-10-11 UTC；其发布说明另提供升级 OpenCode 2 的入口，本次未把 OpenCode 2 的行为计入已确认能力。

## 1. 结论与替换范围

可以让 Agent.Matrix 作为 ACP Client，驱动 OpenCode 作为 ACP Agent，从而替换当前 MAF/MEAI 的模型与工具执行循环。ACP 指 **Agent Client Protocol**。OpenCode 原生提供 `opencode acp`，通过子进程 stdio 交换 JSON-RPC 消息，服务端应用也可以实现这个 Client。[官方 ACP 说明](https://opencode.ai/docs/acp/)、[ACP transport](https://agentclientprotocol.com/protocol/v1/transports)。

推荐先引入第二种执行后端，按 Agent 或新会话选择 MAF/OpenCode。通过验收后再评估默认后端与旧实现的去留。编码、工作区操作、开发任务最适合作为首批场景；通用业务 Agent 在业务工具、配置与历史语义完成适配后才适合迁移。[推断]

| 当前职责 | 可否交给 OpenCode | 平台仍需负责的部分 |
|---|---|---|
| 模型调用、工具循环 | 可以 | 模型授权、配置映射、整体执行期限与预算 |
| 代码搜索、编辑、shell、开发类 Skill | 可以 | 实际运行环境、文件边界、进程和网络限制 |
| MCP 工具调用 | 可以接入 | 服务凭据、授权、动态绑定和失败处理 |
| C# 能力、RAG、用户资料、平台文件工具 | 不能直接搬过去 | MCP 桥接及业务结果转换 |
| HTTP/SSE、渠道、Router | 保留 | 将 ACP 事件转换为现有公开协议 |
| 租户身份、Agent/模型权限 | 保留 | 每次执行前校验和每次业务工具调用校验 |
| 会话目录、用户可见历史、文件资产 | 保留 | 与 OpenCode 执行会话的映射与恢复 |
| Runner/Bubblewrap | 不会被 ACP 自动替代 | 为 OpenCode 进程提供隔离，或继续通过工具调用 Runner |

收益是减少自建 coding agent 循环、开发工具与上下文能力的维护。代价是新增外部运行时、持久状态、版本兼容与适配层；平台不会仅凭换协议就变成无状态服务。[推断]

## 2. 当前仓库实际边界

当前不是一个可直接更换的 `IAgentExecutor` 插件入口：Host 注入具体 `AgentExecutor`，后者直接调用 `AgentFactory` 和 MAF Agent。`IAgentRuntimeResolver` 解析配置并做授权，不是执行后端接口。

| 源码 | 当前责任 | 对迁移的影响 |
|---|---|---|
| `Backend/src/OpenAgent.Core/Execution/AgentExecutor.cs` | 请求解析、文件准备、同步/流式执行、历史与 usage 完成 | 保留平台入口，抽出执行后端选择与结果映射 |
| `Backend/src/OpenAgent.Core/Execution/AgentFactory.cs` | 模型客户端、能力与 Feature、工具隔离、FICC、ChatClientAgent | OpenCode 接管循环；不能继续依赖此处自动包裹其内置工具 |
| `Backend/src/OpenAgent.Core/Execution/AgentRuntimeResolver.cs` | 租户配置、Agent/模型权限、Skill 租户检查 | 在 OpenCode 分支前继续执行 |
| `Backend/src/OpenAgent.Core/Execution/AgentExecutionScope.cs` | MAF Agent 与历史/资源的生命周期 | 现有 Scope 强绑定 SDK，需要后端无关的轮次/历史边界 |
| `Backend/src/OpenAgent.Core/Conversation/History/PlatformChatHistory.cs` | 在 MAF 历史回调中获取会话锁、恢复历史、失败部分保存 | ACP 分支不会触发回调，必须显式保留锁与落库 |
| `Backend/src/OpenAgent.Contracts/Requests/AgentStreamEvent.cs` | Content/Reasoning/ToolCall/ToolResult/Usage/PlanUpdated | 适配基本事件，另设计授权和询问交互 |
| `Backend/src/OpenAgent.Core/Extensions/RuntimeServiceExtensions.cs` | 当前具体执行器注册 | 作为后端注册/选择的组合入口 |

`AgentFactory` 当前还设定 MaxTurns、工具超时、结果截断、写入类工具串行和延迟发现。接入 OpenCode 后，这些规则不会自动继承。应区分可映射的 OpenCode 设置、MCP 服务端执行规则和平台 watchdog，并逐项验收。

现有 [Agent Provider 集成](./agent-provider.md) 属于 Router 对外服务转发，可用于隔离试验入口。其会话亲和定位的是 Provider，不能替代 OpenCode 会话的 worker/存储亲和。若目标是替换本仓库内部执行层，主要改造位置仍是 Core 执行边界，而不是把 Router 改成 ACP 进程管理器。

## 3. 发布版与滚动文档有差异

应绑定 OpenCode 发布版本、ACP 握手版本和能力表。不能把 `/v2/docs/` 理解为 OpenCode 已使用 ACP v2。

对 `v1.18.35` 发布源码的核对结果：

- `initialize` 返回 ACP `protocolVersion: 1`；提供 new/load/resume/close/list、fork 及配置切换入口。扩展能力必须以实际握手为准。[service.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/acp/service.ts)、[agent.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/acp/agent.ts)。
- ACP CLI 会 `Server.listen`，再用内部 OpenCode HTTP SDK 驱动它。网络默认是 `127.0.0.1`、端口 `0`，配置可能覆盖；ACP 外部通道仍为 stdio。[acp.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/cli/cmd/acp.ts)、[network.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/cli/network.ts)。
- 此版本声明 MCP HTTP/SSE 能力。注册通过 `sdk.mcp.add({ directory, name, config })`，不是纯 session 私有注入；注册失败被忽略，需独立验证工具就绪。[service.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/acp/service.ts)。

[官网新版 ACP 页面](https://opencode.ai/v2/docs/cli/acp/) 描述了无 ACP 网络端口、SSE=false、更多会话扩展和较完整的 usage 等行为，其中部分与发布源码不同。这些是后续版本候选能力，不能作为此发布版的交付承诺。

ACP 官方已提供 v2 迁移资料，但仍标注整体 v2 surface 为 draft；v2 的 prompt 响应仅确认接收，结束状态由通知报告，与 v1 有实质区别。首个接入版本建议明确使用 v1；以后单独增加协商和适配。[v2 migration](https://agentclientprotocol.com/protocol/v2/migration)。

## 4. 推荐接入形态

```text
现有前端 / 渠道
      │ HTTP / SSE
Router → Engine.Host → 平台执行入口
                         ├─ 身份、授权、文件、会话锁、追溯与历史
                         └─ 执行后端选择
                              ├─ MAF 后端
                              └─ ACP Client → OpenCode worker
                                               │
                                               ├─ 模型与工具循环
                                               ├─ 隔离工作区、配置、执行历史
                                               └─ 平台 MCP Bridge
                                                    └─ RAG / Files / 业务能力 / Runner
```

以上是建议架构，尚未实现。ACP 适配可以位于 Core 的集成域，通过纯内部抽象供 Execution 使用；跨项目 worker 协议再放 Contracts。Host 做最终组装。继续保持现有八项目引用方向，不能为接入使 Core 引用 Runner 实现，也不能让 Execution 依赖具体业务能力。新增内部域与依赖按架构测试规则审查。

第一阶段在隔离 worker 内由 .NET Client 启动 OpenCode，管理 stdio、stderr、RPC 请求、反向 RPC、事件队列、退出和 watchdog。若 worker 在远程节点，增加平台认证的 worker API，由远端管理 stdio；这不是直接使用成熟的 ACP HTTP 服务。ACP Streamable HTTP 在当前 v1 transport 文档中仍为草案。[ACP transport](https://agentclientprotocol.com/protocol/v1/transports)。

建议先一会话一个进程/隔离实例，便于明确故障与权限边界；在资源数据明确后再考虑共享进程。进程分离本身不构成安全隔离，仍需限制 OS 文件、网络和资源。[推断]

## 5. 必须完成的适配

### 5.1 事件与交互

ACP 是双向 RPC。Client 在 `session/prompt` 等待期间仍须读取通知、处理权限请求并写回响应，不能串行等待 prompt 后再处理权限，否则会挂起。

| ACP v1 消息 | 平台映射建议 |
|---|---|
| `agent_message_chunk` | Content；分别处理文本、图片、资源 |
| `agent_thought_chunk` | Reasoning；仅保存/展示实际收到的内容 |
| `tool_call` | ToolCall，以 session + toolCallId 关联 |
| `tool_call_update` | 合并增量状态；完成/失败才形成 ToolResult |
| `plan`（Agent 实际发送时） | 转换为现有 PlanUpdated JSON |
| prompt 响应 `stopReason` | 结束、取消、限制等平台语义；保留已有 SSE done/error 行为 |
| `session/request_permission` | 平台授权策略或审批交互；不能自动全放行 |
| 询问扩展（协商支持时） | 明确问题展示、超时、取消和恢复规则 |

ACP 工具通知用于报告执行，不能据此接管工具调用。tool `name` 在规范中是可选字段；发布版 `tool.ts` 的初始通知只有展示 title/kind/rawInput 等信息，不能把 title 当稳定的工具名。平台工具可从 MCP Bridge 记录确定名称、输入和输出，内置工具则允许缺少精确名称。[tool calls](https://agentclientprotocol.com/protocol/v1/tool-calls)、[tool.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/acp/tool.ts)。

当前公开事件没有权限待处理/用户回答协议；完整交互接入需要新的事件及回答端点。最小试点也可以采用明确、可审计的服务端策略处理 permission，未定义动作拒绝。不得声明没有实现的客户端能力。

### 5.2 会话与恢复

平台保留用户可见会话和审计；OpenCode 保有执行上下文。建议持久化映射键：tenant/user/conversation + backend + ACP sessionId + worker/storage 标识 + 工作区 + 配置版本。客户端不能自由选择别人的 sessionId/cwd。

推荐给新会话启用后端，并在会话生命周期内固定。OpenCode 的数据库、工作区和附件必须持久化，故障重启重新握手后再 load/resume。`session/load` 会回放历史，必须区分回放与新事件，避免重复写库和重复推送；resume 仅在能力存在时使用。[session setup](https://agentclientprotocol.com/protocol/v1/session-setup)。

ACP 没有把平台任意结构化 MAF 历史原样导入 OpenCode 的通用接口。旧会话迁移应单独设计，不能把一段摘要当作完整恢复。失去 OpenCode 状态时标记需恢复/重新开始，不应静默切回 MAF 接着执行。[推断]

平台 IdempotencyKey 也不等于外部任务恰好执行一次。连接中断可能发生在命令执行之后；重试前需判断轮次状态，写入类业务工具要独立处理重复调用，不能盲目重发 prompt。

### 5.3 模型、Agent 与 Skill 配置

平台 LlmProfile 是租户级 provider/endpoint/key/model 参数；ACP 会话模型选项主要指向 OpenCode 已配置的 provider/model。需要生成隔离的 provider 与 Agent 配置，映射 Instructions、模型、temperature、步骤限制、Skill 和 MCP 绑定；配置支持程度逐项核对，不能假定 ACP prompt 接受全部平台参数。[OpenCode config](https://opencode.ai/docs/config/)、[Agents](https://opencode.ai/docs/agents/)。

OpenCode 自有 Skill 发现/加载方式与当前 SkillFeatureFactory 的 SDK provider、资源读取和 Runner 脚本执行不同。授权后的 Skill 包可投影到 worker 的受控目录，但这只是迁移方案，不能承诺原行为完全一致。[OpenCode skills](https://opencode.ai/docs/skills/)。

密钥只注入目标 worker 的受控环境/配置，不送入 prompt、前端或原始日志；配置、缓存和数据目录要按隔离边界分开。模型调用发生在 OpenCode 后，现有 .NET IChatClient 的完整请求/响应捕获与 OTel 记录不能自动覆盖它。[推断]

### 5.4 业务工具与隔离

MCP Bridge 复用平台授权与业务服务，并独立执行超时、结果预算和并发控制。使用平台签发的短期会话凭据绑定 tenant/user/conversation/agent；不信任模型填写的租户参数，也不把模型 API key 当作业务授权令牌。

发布版 MCP 注册具有 directory 作用域，多会话共用目录/配置时可能互相影响。不同用户的工具配置和凭据不能依赖 sessionId 自然隔离；首期使用独立工作区、数据与配置环境，并验证同目录/同名 MCP 的覆盖和泄漏风险。[推断，依据上述 service.ts 注册路径]

OpenCode 的 permission 区分 allow/ask/deny，默认许多工具直接允许。ACP 权限回调只覆盖实际触发 ask 的动作，并非所有工具的强制拦截层。内置 shell、网络和文件操作也不会走当前 IsolatedToolFunction 或 Bubblewrap。[permissions](https://opencode.ai/docs/permissions/)。

有两种可评估的执行环境：把整个 OpenCode worker 放进合适的容器/沙箱；或限制内置执行工具，把需要受控执行的动作暴露成 Runner MCP 工具。后者需要验证 OpenCode 工具发现和 Agent 配置的兼容性。当前 [Code Runner](./code-runner.md) 不能仅通过换启动命令就视为已支持 OpenCode。

### 5.5 usage 与预算

发布版 prompt 响应从返回的 assistant info 构建 usage；`usage_update.used` 取最新 assistant 的上下文 token，而 cost 汇总会话。不能把这些字段直接作为整轮所有模型调用、所有子 Agent 的计费数据。[usage.ts](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/acp/usage.ts)。

需要验证多次模型调用、缓存 token、reasoning token、子任务和重试口径；无法覆盖的部分标注不可用或近似。ACP 的展示通知不能单独充当硬预算执行器。平台 watchdog 负责整体期限，实际模型/工具预算由 worker 与工具服务端落实。[推断]

## 6. ACP 与 OpenCode HTTP API 的选择

| 维度 | ACP | OpenCode HTTP API |
|---|---|---|
| 多 Agent 后端互换 | 协议层更适合 | 与 OpenCode API 绑定 |
| 驱动同节点受控 worker | stdio 合适 | 也可使用内部认证服务 |
| 独立远程服务 | 需要平台 worker 通道 | 原生 HTTP/OpenAPI/SSE 更直接 |
| 平台消息、diff、todo 等专有能力 | 标准或扩展未必完整 | 可查询 OpenCode 专有对象 |
| 长期维护 | 管理 ACP 版本与实现差异 | 管理 OpenCode API 版本 |

OpenCode Server 提供会话、消息、abort、diff、todo、异步 prompt 和事件流。若确定长期只接 OpenCode 且主要是远程 worker，HTTP 值得一起验证；如果希望未来复用协议接入其他 Agent，ACP 更有价值。HTTP 方案仍需要相同的平台授权、状态和隔离适配。[Server API](https://opencode.ai/docs/server/)。

## 7. .NET 8 可行性

官方库目录把 .NET 实现列为社区库，并非官方 .NET SDK。[community libraries](https://agentclientprotocol.com/libraries/community)。

| 候选 | 此次源码检查 | 建议 |
|---|---|---|
| `nuskey8/acp-csharp` / AgentClientProtocol | csproj 包含 net8.0、netstandard2.1 等目标 | 首个 PoC 候选，验证反向 RPC、取消、未知字段及所需 v1 能力 |
| `MertBasar0/acp-net` | Process/Testing 目标 net8.0；项目明确是进程运行与测试工具 | 可用于生命周期；不能当完整协议 SDK |
| `AgenticDesktop/ACPLibrary` | 当前 csproj 为 net10.0 | 不符合仓库 .NET 8 约束，不直接采用 |

来源：[acp-csharp csproj](https://github.com/nuskey8/acp-csharp/blob/main/src/AgentClientProtocol/AgentClientProtocol.csproj)、[Acp.Net](https://github.com/MertBasar0/acp-net)、[ACPLibrary csproj](https://github.com/AgenticDesktop/ACPLibrary/blob/main/Agentic.ACPLibrary.csproj)。这是目标框架核对，没有做 NuGet restore 或实际互通测试。

若社区 SDK 不能覆盖所需能力，可以使用 .NET Process + System.Text.Json 实现必要的双向 RPC 子集；须实现并发请求关联、通知和反向请求分发、单一写入队列、异常与取消传播、输出限额及未知字段兼容，工作量不能按简单 CLI 包装估计。[推断]

## 8. 分阶段验证与采用门槛

1. **协议 PoC**：固定 OpenCode 版本；initialize/new、流式文本、工具通知、permission 往返、cancel、close、进程异常和 restart/load。先验证 .NET 8 社区 SDK 是否可用。
2. **平台试点**：只启用新会话；保留当前授权/会话锁/HTTP-SSE；接一个只读 RAG 工具和一个带副作用工具，验证 MCP Bridge、文件产物及配置映射。
3. **运行验收**：多租户隔离、同名 MCP、多实例亲和、存储恢复、usage、超时、子进程清理和模型服务失败。与现有 MAF 后端对比任务完成率、成本和延迟。
4. **迁移决策**：验收通过后扩大 Agent 范围；旧会话迁移、默认后端切换和 MAF 去留分别决策。

| 必测场景 | 验收要求 |
|---|---|
| permission 在 prompt 期间返回 | 双向 RPC 无死锁；允许/拒绝/取消可追溯 |
| 同一会话两个请求 | 平台会话锁继续生效，不交错历史 |
| 文本/工具流中断 | 保存部分结果，轮次失败/取消状态正确 |
| worker 重启 | 恢复原 session 与工作区；回放不会重复入库 |
| 执行后失去连接 | 不盲目重发写操作；显示不确定状态 |
| 两个租户、同名工具和相似目录 | 文件、密钥、模型配置与工具绑定无跨租户访问 |
| MCP 不可用 | 不把无工具运行误认为配置成功 |
| 长命令/子进程/取消 | 真实结束进程，资源回收；不是仅更新界面状态 |
| 多模型调用和子任务 | usage 口径清楚，预算与成本可核对 |
| 非编码业务任务、附件与产物 | 对现有 Agent 的授权、数据格式与成功条件保持可用 |

当前完成的是静态可行性调研。这些验证尚未执行，不能据本文件宣称生产替换完成。建议下一步实施协议 PoC 和有限平台试点，暂不移除 MAF。

## 9. 重新评估：替换量与统计口径

本节以最终只使用 OpenCode 执行、保留现有产品功能为目标。若永久并行维护 MAF，则旧运行实现仍需保留，下述退役候选不能实际删除。

统计 `Backend/src/**/*.cs`，排除 bin/obj；行数为物理行，包含注释和空行。不是逻辑 SLOC，不包括测试、前端、Python 沙箱、部署及文档，也不代表开发成本。Core 共 120 文件/12,344 行；全后端共 412 文件/39,417 行；排除数据库 Migrations 后为 388 文件/33,280 行。

| 范围 | 文件 | 原实现行数 | 占 Core | 占后端（排除迁移） | 含义 |
|---|---:|---:|---:|---:|---|
| 执行与 SDK 适配主体 | 40 | 4,591 | 37.2% | 13.8% | 包括将退役和需要重写的实现 |
| MCP/Skill/计划/代码与工作区运行适配 | 13 | 2,027 | 16.4% | 6.1% | 可交给原生能力或改成桥接/插件 |
| 合计受影响主体 | 53 | 6,618 | 53.6% | 19.9% | 对含迁移的后端为 16.8% |
| 上述合计中的整体退役候选 | 22 | 1,323 | 10.7% | 4.0% | 完成等价功能与调用方迁移后，可移除原文件 |
| 上述合计中的混合职责改写候选 | 31 | 5,295 | 42.9% | 15.9% | 有业务规则需拆出/复用，不能按整文件算删除 |

整体退役候选包括 AgentFactory/AgentExecutionScope、SDK 模型客户端、历史 Provider 工厂、Feature 绑定和延迟工具注入等。这里的“整体退役”仍以迁出配置、授权和治理规则为前提，不意味着这些规则被 OpenCode 自动继承。

| 主体分组 | 文件 / 行数 | 原实现变化 | 继续承担的责任 |
|---|---|---|---|
| 执行组装（Execution 除 RuntimeResolver） | 4 / 709 | 工厂/Scope 退役，执行入口重写 | 请求校验、授权、锁、事件、结束与失败落库 |
| ModelProviders | 3 / 283 | 模型 SDK 适配交给 OpenCode | 配置编译；硬限制与审计移到模型网关/插件 |
| Conversation/History | 6 / 962 | MAF history provider 退役 | 原会话存储、部分结果和附件关联由事件写入器承担 |
| Conversation/Compaction | 3 / 547 | 压缩算法交给 OpenCode，平台手动压缩端点重写 | 权限、锁、压缩审计、产品设置兼容 |
| Mapping 的消息/响应/工具转换 | 3 / 649 | SDK DTO 转换改为 ACP/OpenCode 转换 | wire 格式、文件元数据和错误语义 |
| Tooling | 15 / 818 | AIFunction/Feature/延迟发现机制退役或重写 | 超时、串行、输出预算与授权规则移到服务端/插件 |
| Observability 的模型捕获（不含 EngineMeter） | 6 / 623 | DelegatingChatClient 捕获改为模型网关记录 | 现有交互记录存储、查询与身份追溯 |
| MCP 运行适配选定文件 | 7 / 948 | 原生直连可替代客户端循环；代理模式继续复用部分实现 | MCP 管理测试、二进制资产落库、资源访问控制 |
| Skill Provider 绑定 | 3 / 380 | SDK Provider/Runtime 退役，授权包部署重写 | 包校验、目录穿越防护、租户选择与脚本许可 |
| Plan | 1 / 168 | 可变成轻量自定义工具或保留平台工具 | 现有 PlanUpdated 结构和步骤校验 |
| Code/Workspace 工具 | 2 / 531 | 可改成 OpenCode bash/read/edit 等 | 隔离环境、产物发布和受控执行策略 |

明确不计入替换量的主体包括：Contracts、Router、Hosting、Engine 配置与注册、Host 的业务端点、Infrastructure 的持久化、Core 的 Security、Files 业务服务、RAG 检索适配器，以及会话 Store/Query/Lock。它们仍为现有产品提供功能。

Runner 另有 11 个 C# 文件/1,873 行及 Python 沙箱代码。若整个 OpenCode worker 提供等价执行隔离，Runner 的某些实现可以再次评估退役；若保留受控 execute_code/run_skill_script，则继续使用。不能把 bash 工具的存在当作 Runner 已被替代，也不把这 1,873 行加进主表。

以上是源码范围核对。净维护量需要扣除新增 ACP/HTTP 适配、MCP Bridge、插件、模型网关和 worker 生命周期代码；尚未实现，不能可靠给出净删减百分比。[推断]

## 10. 无法直接迁移的功能：具体完成方式

上次把 C# 能力概括为“需要 MCP 桥接”过于笼统。业务能力不必全部重写，运行机制也不必全部桥接。以下按最合适的边界选择。

### 10.1 RAG、身份资料及 C# 业务能力

首选把现有服务暴露为平台 MCP 工具：`search_knowledge_base` 仍调用 RagService/QdrantAdapter/RagFlowAdapter，`get_current_user_profile` 仍读取已验证身份。无需把检索、权限和配置迁入 TypeScript，也无需重写数据库实现。

现有 CapabilityDefinition 的 schema 和异步委托可以继续作为平台工具定义；把 CapabilityToolFactory 的 AIFunction 绑定替换成 MCP Server 绑定。MCP 请求创建独立 DI scope，并从短期服务凭据还原经过校验的 TurnContext，不可复用聊天 HTTP 请求的 AsyncLocal 或 scoped 服务。

替代路径是 OpenCode custom tool 调用平台 HTTP API，适合已有业务端点或只绑定 OpenCode 的部署。工具定义需要少量 JS/TS，但服务实现仍可保持 C#。这一方案与 MCP 二选一，避免同时维护两套业务契约。[custom tools](https://opencode.ai/docs/custom-tools/)。

### 10.2 平台文件、附件、产物及外部 MCP 二进制资源

平台 FileAssetService/FileShareService 和 S3 数据继续保留。OpenCode 的 read/write 针对工作区路径，当前 read_file/write_file 针对授权 fileId、登记新资产和会话引用，语义并不相同。

建议提供平台文件 MCP：接入时下载授权附件到受控目录；工作区产物通过 `register_artifact`/既有 publish_files 服务上传、注册和关联消息；create_file_transfer_url 的有效期与下载限制继续由平台执行。平台工具的 write_file 不改成原生 write，以免把“新建资产”误变成“覆盖磁盘文件”。

任意外部 MCP 返回的 blob 不保证自动变成平台 fileId。需要保留此行为时，让该 MCP 经平台代理，复用 McpResourcePipeline 的上传与结果重写逻辑，并在模型看到结果之前处理。可只代理需资产/权限治理的服务器，其余允许原生直连。[推断]

单纯在 ACP 流里看到工具结果之后再上传，无法同时保证模型已收到的平台文件引用语义。产物也不能只在轮次结束时扫描：失败/取消会丢失即时关联，且扫描可能暴露输入或敏感文件。使用显式注册，加受控恢复扫描。[推断]

### 10.3 Skill、脚本及工作区执行

Skill 的发现与读取可由 OpenCode 原生 skill 工具承担。平台继续做包授权、hash/结构校验和受控部署；只投影当前 Agent 有权使用的包，不扫描 Engine 用户的个人全局 Skill。[skills](https://opencode.ai/docs/skills/)。

脚本执行有两个完整方案：

- 保留 Runner：把 execute_code/run_skill_script 暴露为 MCP 或 custom tool，复用执行限制与产物管道。适合必须保持现有 ScriptExecutionEnabled/CodeExecution 双重开关和 Python/JS/bash 行为的业务 Agent。
- 让整个 OpenCode 运行在受限 worker：原生 shell 与包脚本共用受控工作区，worker supervisor 落实资源、网络、运行期限、进程树终止、TTL 回收与产物登记。适合主要做开发任务，并接受执行环境语义变化的 Agent。[推断]

当前 Runner 是特定语言执行环境，不应不经验证直接改成启动 OpenCode。若要兼容单包脚本授权，开放普通 bash 后不能仅靠提示词禁止脚本；应从包投影、文件可见性、工具入口和 worker 权限真正限制绕过。[推断]

OpenCode custom tool 可有意覆盖内置同名工具，发布版工具注册逻辑也支持插件工具。不过把 bash 改为远程 Runner wrapper 会影响 cwd、文件路径和状态，仍需联通测试；它是可选方案，不能视为零改造。[custom tools](https://opencode.ai/docs/custom-tools/)、[v1.19.0 registry](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/tool/registry.ts)。

### 10.4 计划、工具治理和动态授权

Plan 可以保留现有 update_plan 校验，暴露为平台工具；或做小型 custom tool，输出完全兼容的 JSON，再由事件适配生成 PlanUpdated。不能假定所有 OpenCode 版本都输出标准 ACP plan，后者需要以实际通知为准。

工具超时、输出截断与写入串行优先在 MCP/业务服务端执行。内置工具由受信任插件/自定义工具处理应用策略，实际 shell 生命周期由 supervisor 控制。最新核对的插件定义存在 tool.execute.before/after 和 chat.params，适合元数据关联、参数和输出处理；before/after 不构成通用的超时执行器，也不是 OS 安全边界。[plugin types](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/plugin/src/index.ts)。

Agent、模型、Skill 和业务资源授权继续由平台决定。OpenCode permission 是动作许可/审批：平台可把策略编译成配置，业务工具调用时再做动态授权。只有在 ask 动作时才会产生 permission RPC，不能借此覆盖所有操作。

### 10.5 会话历史、压缩与旧会话迁移

OpenCode 数据库持有推理上下文，平台数据库持有会话归属、用户可见历史、文件引用与审计。新增事件写入器把流式正文、思考、工具、失败和取消落入现有 Store，并维护执行映射、回放去重和 worker/storage 亲和。现有 PostgreSQL/Redis 可以继续使用。

自动/手动压缩都可以转交 OpenCode；手动压缩端点改成 worker 调用。发布源码支持 compaction agent 的模型选择、近期上下文选择以及压缩 prompt 插件扩展；足以实现替代方案，但不保证 MAF 的 PreserveRecentTurns、summary token 和审核记录口径完全相同。[v1.19.0 compaction](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/session/compaction.ts)。

若需要保留原压缩业务配置，可编译为受控 OpenCode 配置并保留平台压缩审计；涉及 experimental hook 的定制固定插件版本。要求逐字段一致时，需要专门适配或继续让这类旧会话使用 MAF。

旧会话迁移可选三种产品行为：旧会话在过渡期继续 MAF；用户明确创建新 OpenCode 会话并携带历史摘要/附件；或构建版本绑定的历史导入器并专项验收。摘要可以让任务继续，但不是无损恢复。不能把实验性 message transform hook 当作受支持的任意历史导入 API。[推断]

### 10.6 模型参数、usage、成本与完整调用审计

配置编译映射 provider/endpoint/model、Agent instructions、temperature 和 steps。v1.19.0 的 chat.params 还提供 maxOutputTokens；这些可以替代当前 SDK 的参数注入。steps 与现有 FICC iterations 语义不同，需要测试，不能承诺数字相同就行为一致。[agents](https://opencode.ai/docs/agents/)、[plugin types](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/plugin/src/index.ts)。

若保留当前完整模型交互审计、统一鉴权和硬预算，建议令 OpenCode 的模型 endpoint 指向平台模型网关：网关持有真实 provider key、验证短期 execution token、记录真实请求/响应和 usage，并在每次调用前执行配额。现有 ILlmInteractionStore 与查询可继续复用，LlmInteractionRecorder 的 SDK 包装层退役。[推断]

网关需分别验证当前使用的 ChatCompletions、Responses、Anthropic Messages 及流式/多模态，不能假定所有 provider 都是同一种 OpenAI JSON；压缩、标题和子 Agent 调用也必须经过网关。插件 headers 为请求附带 session/trace 元数据，凭据不能由模型填写。

只需显示 usage 时，可以查询 OpenCode 消息和 cost，不建模型网关；但会失去网关层的完整原始调用审计和统一硬预算。请求计数可把重试单独归类，实际 provider 计费以可核对 usage 为准。严格总额限制还需要预留本次最大消费与并发额度，不能等终轮 usage 再扣额度。[推断]

## 11. 三种完整替代方案

| 方案 | 能保留的功能 | 对旧实现的退役效果 | 主要代价 |
|---|---|---|---|
| A. ACP + 平台 MCP + 保留 Runner | 最易保留 RAG、资产、业务权限、旧脚本与文档运行时 | MAF 绑定可退役；Runner/业务服务保留 | 双执行工作区需要明确路径和附件映射 |
| B. OpenCode worker + 平台服务 + 薄插件 + 模型网关 | 覆盖业务能力、模型审计、产物、开发任务；执行隔离由 worker 提供 | 可进一步评估退役旧 Code/Workspace/Runner 实现 | worker 环境、生命周期和资源限制要重新验收 |
| C. 按 Agent/会话分流，业务 Agent 保留 MAF，开发 Agent 用 OpenCode | 保留严格旧行为，也可使用新运行时 | 主要是增加能力；旧实现通常不能删除 | 永久维护两套循环，难实现简化目标 |

建议以 A 完成产品等价试点，再按任务类型演进到 B。C 可作为有退出条件的过渡，而不是默认永久架构。若最终目标明确是“把执行层交给 OpenCode”，应定义关闭 MAF 的验收条件；不能以“继续保留旧功能”无限延长双实现。[推断]

ACP 和 HTTP 是控制通道选择，不改变上述功能分工。希望复用多个 Agent 时选 ACP；若只使用 OpenCode，且很依赖消息查询、恢复、压缩与产物管理，OpenCode Server HTTP/OpenAPI/SSE 可以降低专有能力补齐成本。先保留平台内部后端接口，再按需求选通道，没必要同时实现两套全量适配。[Server API](https://opencode.ai/docs/server/)。

## 12. 真正不能保证等价的部分与验收重点

业务功能大多有替代完成路径。真正的限制是原执行语义不能仅凭 ACP 保证完全相同：

- 任意旧 MAF 历史无损恢复、原模型消息布局与系统提示词不能自动保持。
- 原 SDK 工具 schema、延迟发现、迭代次数、串行策略与上下文压缩策略需要映射或迁出。
- OpenCode 不替代企业租户、文件资产、对象存储和业务资源授权，仍需要平台服务。
- 外部副作用恰好执行一次、失败后的盲目续跑不能由协议自动保证；服务需按 invocationId 去重、轮次需有未确认状态。
- 成本、延迟、任务质量与净代码减少量需要真实任务对比，当前不能从代码统计推出。

验收应按用户任务验证完整闭环，而不是只确认可以聊天：带附件做 RAG 查询并引用来源；执行授权 Skill 并返回下载产物；取消长命令后进程确实结束；重启 worker 后续聊无重复历史；两租户访问同名工具无泄漏；多次模型调用、子任务和压缩全部有审计；旧会话的迁移行为得到明确处理。

## 13. 替换范围文件清单

以下路径均相对 `Backend/src/OpenAgent.Core/`。清单的分类是评估建议，不是已执行删除；“整体退役候选”需要先完成前述规则迁出，“拆分/改写”保留或复用其中平台语义。每个文件只计一次，合计 53 文件/6,618 物理行。

| 文件 | 分组 | 物理行 | 处理建议 |
|---|---|---:|---|
| `Execution/AgentCompositionLog.cs` | 执行组装 | 14 | 整体退役候选 |
| `Execution/AgentExecutionScope.cs` | 执行组装 | 74 | 整体退役候选 |
| `Execution/AgentExecutor.cs` | 执行组装 | 368 | 拆分/改写 |
| `Execution/AgentFactory.cs` | 执行组装 | 253 | 整体退役候选 |
| `ModelProviders/AgentChatClientFactory.cs` | 模型适配 | 186 | 整体退役候选 |
| `ModelProviders/IAgentChatClientFactory.cs` | 模型适配 | 21 | 整体退役候选 |
| `ModelProviders/OutputTokenLimitedChatClient.cs` | 模型适配 | 76 | 整体退役候选 |
| `Conversation/History/ConversationHistoryFactory.cs` | MAF历史 | 192 | 整体退役候选 |
| `Conversation/History/IPlatformChatHistoryFactory.cs` | MAF历史 | 6 | 整体退役候选 |
| `Conversation/History/PlatformChatHistory.cs` | MAF历史 | 726 | 拆分/改写 |
| `Conversation/History/PlatformChatHistoryContext.cs` | MAF历史 | 12 | 整体退役候选 |
| `Conversation/History/PlatformChatHistoryFactory.cs` | MAF历史 | 10 | 整体退役候选 |
| `Conversation/History/TurnConversationExtensions.cs` | MAF历史 | 16 | 整体退役候选 |
| `Conversation/Compaction/AuditedCompactionStrategy.cs` | MAF压缩 | 355 | 拆分/改写 |
| `Conversation/Compaction/ConversationCompactionLog.cs` | MAF压缩 | 38 | 拆分/改写 |
| `Conversation/Compaction/ConversationCompactionService.cs` | MAF压缩 | 154 | 拆分/改写 |
| `Mapping/AgentMessageAdapter.cs` | SDK消息转换 | 464 | 拆分/改写 |
| `Mapping/AgentResponseAdapter.cs` | SDK消息转换 | 60 | 整体退役候选 |
| `Mapping/ToolResultText.cs` | SDK消息转换 | 125 | 拆分/改写 |
| `Tooling/Abstractions/AgentFeature.cs` | 工具组装治理 | 17 | 整体退役候选 |
| `Tooling/Abstractions/CapabilityDefinition.cs` | 工具组装治理 | 19 | 拆分/改写 |
| `Tooling/Abstractions/IAgentFeatureFactory.cs` | 工具组装治理 | 14 | 整体退役候选 |
| `Tooling/Abstractions/ICapabilitySource.cs` | 工具组装治理 | 14 | 拆分/改写 |
| `Tooling/Abstractions/IToolConcurrencyProvider.cs` | 工具组装治理 | 11 | 拆分/改写 |
| `Tooling/Abstractions/ToolConcurrency.cs` | 工具组装治理 | 16 | 拆分/改写 |
| `Tooling/Discovery/CapabilityToolFactory.cs` | 工具组装治理 | 180 | 拆分/改写 |
| `Tooling/Discovery/DeferredToolCatalog.cs` | 工具组装治理 | 94 | 整体退役候选 |
| `Tooling/Discovery/DeferredToolInjector.cs` | 工具组装治理 | 54 | 整体退役候选 |
| `Tooling/Discovery/ToolSearchFunction.cs` | 工具组装治理 | 77 | 整体退役候选 |
| `Tooling/Invocation/AgentExecutionOptions.cs` | 工具组装治理 | 28 | 拆分/改写 |
| `Tooling/Invocation/IsolatedToolFunction.cs` | 工具组装治理 | 195 | 拆分/改写 |
| `Tooling/Invocation/ToolConcurrencyRules.cs` | 工具组装治理 | 19 | 拆分/改写 |
| `Tooling/Invocation/ToolInvocationScope.cs` | 工具组装治理 | 13 | 拆分/改写 |
| `Tooling/Invocation/ToolResultBudgets.cs` | 工具组装治理 | 67 | 拆分/改写 |
| `Observability/LlmInteractionCapture.cs` | 模型交互捕获 | 17 | 拆分/改写 |
| `Observability/LlmInteractionLog.cs` | 模型交互捕获 | 36 | 拆分/改写 |
| `Observability/LlmInteractionPayload.cs` | 模型交互捕获 | 239 | 拆分/改写 |
| `Observability/LlmInteractionRecorder.cs` | 模型交互捕获 | 229 | 拆分/改写 |
| `Observability/LlmStreamedResponse.cs` | 模型交互捕获 | 85 | 拆分/改写 |
| `Observability/TurnCaptureExtensions.cs` | 模型交互捕获 | 17 | 拆分/改写 |
| `Capabilities/Mcp/McpClientPool.cs` | MCP运行适配 | 183 | 拆分/改写 |
| `Capabilities/Mcp/McpFeatureFactory.cs` | MCP运行适配 | 47 | 整体退役候选 |
| `Capabilities/Mcp/McpFeatureLog.cs` | MCP运行适配 | 10 | 整体退役候选 |
| `Capabilities/Mcp/McpResourcePipeline.cs` | MCP运行适配 | 338 | 拆分/改写 |
| `Capabilities/Mcp/McpResourceReaderTool.cs` | MCP运行适配 | 158 | 拆分/改写 |
| `Capabilities/Mcp/McpToolFactory.cs` | MCP运行适配 | 188 | 拆分/改写 |
| `Capabilities/Mcp/McpToolRuntime.cs` | MCP运行适配 | 24 | 整体退役候选 |
| `Capabilities/Skill/AgentSkillsProviderFactory.cs` | Skill Provider | 314 | 拆分/改写 |
| `Capabilities/Skill/AgentSkillsRuntime.cs` | Skill Provider | 43 | 整体退役候选 |
| `Capabilities/Skill/SkillFeatureFactory.cs` | Skill Provider | 23 | 整体退役候选 |
| `Capabilities/Plan/PlanCapabilitySource.cs` | 计划工具 | 168 | 拆分/改写 |
| `Capabilities/Code/CodeCapabilitySource.cs` | 代码工作区工具 | 158 | 拆分/改写 |
| `Capabilities/Workspace/WorkspaceCapabilitySource.cs` | 代码工作区工具 | 373 | 拆分/改写 |
