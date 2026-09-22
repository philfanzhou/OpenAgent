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
| 最大轮次控制 | 默认 50 轮（`AgentConfig.MaxTurns`，fallback 同为 `DefaultMaxTurns=50`） |

## Architecture
```text
AgentFactory
  ├─ CapabilityToolFactory: 授权过滤 → AIFunction（schema 校验/降级）
  ├─ McpToolFactory: official McpClientTool（mcp__{server}__{tool}）
  ├─ AgentSkillsProviderFactory: official AgentSkillsProvider
  └─ ChatClientAgent / FunctionInvokingChatClient
       Tools = 全部工具 × IsolatedToolFunction.Wrap(超时, 分级预算, logger)
         ├─ ToolResult(IsError) → 错误信封原样回传
         ├─ 成功内容 → 预算截断（头尾保留 + 收窄提示）
         ├─ 超时 → {"error","code":"tool_timeout","timedOut":true}
         └─ 异常 → 脱敏信封（errorId 关联日志）
```

发现阶段执行可见性授权；执行阶段再次校验权限，避免发现与调用之间权限变化。

工具描述按"新员工手册"标准维护：写明何时用/何时不用/失败怎么办/与其他工具的配合（如 `write_file` 明确不能修改已有文件、`read_file` 明确二进制走 publish 路径）。

## Current Status
**Implemented** — 原生 Function Calling、结构化结果契约、统一错误信封、分级结果预算、异常脱敏已落地。

## Limits
- 无并行工具调用（逐个串行执行）
- 无工具调用结果缓存
- 预算按字符计（≈4 字符/token），非精确 token 计数

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/`（CapabilityToolFactory 等）、`Runtime/Agent/IsolatedToolFunction.cs`、`Runtime/Agent/ToolResultBudgets.cs`
- Contracts: `Backend/src/OpenAgent.Contracts/Capabilities/ToolResult.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/CapabilityToolFactoryTests.cs`、`BuiltInToolSchemaTests.cs`、`Runtime/ToolResultBudgetTests.cs` 等
