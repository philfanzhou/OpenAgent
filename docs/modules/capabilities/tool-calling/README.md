# Tool Calling

工具调用体系是模型与外部能力交互的统一机制；RAG/MCP 进入 `ChatOptions.Tools`，Agent Skills 由 MAF `AIContextProvider` 管理其渐进披露工具。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 统一工具集合 | 模型看到 RAG 与官方 MCP `AITool`，无需感知来源差异 |
| 原生 Function Calling | 模型工具调用由 MAF `FunctionInvokingChatClient` 解析并分发（框架反射 `AIFunction`，无手写路由表），结果回写会话继续推理 |
| 结构化结果契约 | `CapabilityDefinition.Invoke` 返回 `ToolResult`（Content/IsError/IsTruncated/TruncationHint/Metadata）；错误走统一信封 `{"error","code","hint"}` 而不是抛异常 |
| 结果预算截断 | `IsolatedToolFunction` 对所有工具结果按分级字符预算做头尾保留截断（读 80k/执行 40k/MCP 48k/默认 60k），截断附收窄提示 |
| 异常脱敏 | 未捕获异常不透传给模型：原始异常进日志（errorId 关联），模型只看到通用失败信封 + hint |
| 工具路由 | `search_knowledge_base`→RAG，`mcp_*`→官方 MCP，`load_skill` / `read_skill_resource`→MAF Skill Provider，`run_skill_script`→隔离 Runner（经 `SkillScriptRunner`，需三层开关） |
| Schema 硬化 | 内置工具 schema 全部封闭（`additionalProperties:false`、参数带 type、required 引用校验）；非法 schema 生产降级+Error 日志、Development 直接抛错；`BuiltInToolSchemaTests` 在 CI 兜底 |
| 并行工具调用 | 同一条 assistant 消息里的多个调用并发执行；仅能力源显式声明 ReadOnly 的工具真正并行（read_file/list_files/search_knowledge_base/get_current_user_profile/update_plan），其余（写入/执行/MCP/Skill）由每轮共享信号量串行化；排队等待计入单次调用超时 |
| web_fetch | 抓取公开 HTTP(S) 页面并转为可读文本（HTML→纯文本，script/style 剥离；text/json/xml 直通；二进制指引 download_file）；复用 FileAssetUrlDownloader 的 SSRF 防护（禁环回/内网、限重定向与大小）；15 分钟进程内缓存；ReadOnly |
| get_context_remaining | 上下文余量近似值（窗口大小 + 已完成轮用量求和 + chars/4 估算，不含在途轮）；窗口未配置时返回不可用 |
| 上下文压缩 | 自动压缩默认开启（`ConversationStore:EnableAutoCompaction`）：80% 阈值触发摘要压缩，摘要请求经 UserMessageEnsuringChatClient 兜底 user 消息（Qwen 系模板兼容） |
| 会话工作区 | 五个 workspace 工具操作宿主侧 session-\<key\>/work（bind-mount 进会话沙箱 /work，与 execute_code 共享状态）：`list_workspace_files`（ReadOnly）/`read_workspace_file`（cat -n 行号 + offset/limit 分页，ReadOnly）/`write_workspace_file`/`edit_workspace_file`（精确字符串替换 + 唯一性校验）/`export_workspace_file`（工作区→file asset→publish 交付桥）；`download_file` 可选 workspacePath 直落工作区 |
| 计划工具 | `update_plan`（对标 Claude Code TodoWrite / Codex update_plan）：全量重发步骤列表，≤1 个 in_progress；结果快照落进会话时间线，SSE 附加 `plan_updated` 事件 |
| MCP 延迟加载 | 可见 MCP 工具超过 `Mcp:DeferredToolThreshold`（默认 20，≤0 关闭）时不整体注入，改由单个 `search_tools` 入口按需检索激活（对标 Codex defer_loading）；激活的工具经与内联一致的隔离包装注入后续请求 |
| 工具列表回归验证 | `AgentExecutorSkillToolTests.SkillsConfigured_SkillToolsAreOfferedToModel` 捕获实际发往模型的 options.Tools，断言 skills 三件套（load_skill/read_skill_resource/run_skill_script）在场——排查"模型调了不存在的工具"时先确认列表没给错 |
| 工具体量观测 | AgentFactory 每轮记录 `Agent tool definitions: N tools, X chars (~Y tokens per request)`，追踪工具吃上下文的实际水位 |
| MCP 连接池化 | `McpClientPool` 按（租户, 服务器地址）缓存客户端跨轮复用；每轮 ListTools 兼作健康探测，坏连接淘汰后立即重连一次；空闲超过 `Mcp:ClientIdleTimeoutSeconds`（默认 600s）惰性淘汰 |
| 最大轮次控制 | 默认 50 轮（`AgentConfig.MaxTurns`，fallback 同为 `DefaultMaxTurns=50`） |

## Architecture
```text
AgentFactory
  ├─ CapabilityToolFactory: 授权过滤 → AIFunction（schema 校验/降级）
  ├─ McpToolFactory: official McpClientTool（mcp__{server}__{tool}）
  ├─ AgentSkillsProviderFactory: official AgentSkillsProvider
  └─ ChatClientAgent / FunctionInvokingChatClient（AllowConcurrentInvocation=true）
       │  DeferredToolInjector（激活的延迟 MCP 工具并入当轮 options.Tools）
       Tools = 全部工具 × IsolatedToolFunction.Wrap(超时, 分级预算, 并发类别, 独占信号量, logger)
         ├─ ToolResult(IsError) → 错误信封原样回传
         ├─ 成功内容 → 预算截断（头尾保留 + 收窄提示）
         ├─ Exclusive → 每轮共享信号量串行（排队计入超时）；ReadOnly → 并发执行
         ├─ 超时 → {"error","code":"tool_timeout","timedOut":true}（排队超时文案可区分）
         └─ 异常 → 脱敏信封（errorId 关联日志）
```

发现阶段执行可见性授权；执行阶段再次校验权限，避免发现与调用之间权限变化。

工具描述按"新员工手册"标准维护：写明何时用/何时不用/失败怎么办/与其他工具的配合（如 `write_file` 明确不能修改已有文件、`read_file` 明确二进制走 publish 路径）。

## Current Status
**Implemented** — 原生 Function Calling、结构化结果契约、统一错误信封、分级结果预算、异常脱敏已落地。

## Limits
- ReadOnly 并行白名单当前为内置读取类工具（含 workspace 读取）；MCP/Skill 工具一律按独占串行（保守口径，待逐类审查后再放开）
- 无工具调用结果缓存（web_fetch 的 15 分钟页面缓存除外）
- 池化连接的复用/空闲淘汰路径无自动化测试（MCP SDK 无公开内存传输），当前靠失败路径单测 + 代码审查保障
- 预算按字符计（≈4 字符/token），非精确 token 计数
- 16 个内置工具全开时定义体量 ≈ 11.3k 字符（~2.8k tokens/请求）；按环境/代理关功能开关（FileAssets、CodeExecution、Rag、Mcp 绑定）收敛，MCP 大目录交给延迟加载
- 每代理按工具名的可见性裁剪曾实现后按维护者决定移除（推迟；如需恢复见方案文档记录）

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/`（CapabilityToolFactory、Plan/PlanCapabilitySource、Mcp/McpClientPool 等）、`Runtime/Agent/IsolatedToolFunction.cs`、`Runtime/Agent/ToolResultBudgets.cs`、`Runtime/Agent/ToolConcurrencyRules.cs`
- Contracts: `Backend/src/OpenAgent.Contracts/Capabilities/ToolResult.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/CapabilityToolFactoryTests.cs`、`BuiltInToolSchemaTests.cs`、`Runtime/ToolResultBudgetTests.cs`、`Runtime/AgentExecutorSkillToolTests.cs`（工具列表在场验证）等
