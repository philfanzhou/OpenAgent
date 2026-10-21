# Tool Calling

工具调用体系是模型与外部能力交互的统一机制。平台把已授权的能力转换为 `RuntimeTool` 交给运行时驱动，
外部 MCP server 则连同定义透传给 harness 原生连接。运行时边界见 [OpenCode 运行时](../../../integrations/opencode-runtime.md)。

## Core Capabilities
| Capability | Description |
|-----------|------------|
| 统一工具集合 | 模型看到平台能力与 skill 工具，无需感知来源差异；外部 MCP 工具由 harness 原生发现 |
| 能力适配 | `CapabilityToolFactory` 按授权筛选 `ICapabilitySource`，把 `CapabilityDefinition.Invoke` 的 `ToolResult` 渲染成模型可见文本 |
| 结构化结果契约 | `CapabilityDefinition.Invoke` 返回 `ToolResult`（Content/IsError/IsTruncated/TruncationHint/Metadata）；错误走统一信封 `{"error","code","hint"}` 而不是抛异常 |
| 结果预算截断 | `IsolatedToolFunction` 对工具结果按分级字符预算做头尾保留截断（读/执行/MCP/默认四档），截断附收窄提示 |
| 异常脱敏 | 未捕获异常不透传给模型：原始异常进日志（errorId 关联），模型只看到通用失败信封 + hint |
| 调用再授权 | 每次调用前重新查授权，避免发现与调用之间权限变化仍沿用旧授权 |
| 并发控制 | 能力源显式声明 `ReadOnly` 的工具可并发；其余工具由每轮共享信号量串行化，排队等待计入单次调用超时 |
| 超时语义 | 单次调用超限即取消，以带 `timedOut` 标记的结果回传模型，由模型决定重试或绕开，不中断整轮 |
| 工具路由 | `search_knowledge_base`→RAG，`load_skill` / `read_skill_resource` / `run_skill_script`→Skill 能力，`execute_code`→隔离 Runner（经 `SkillScriptRunner`，需三层开关） |
| Schema 硬化 | 内置工具 schema 全部封闭（`additionalProperties:false`、参数带 type、required 引用校验）；非法 schema 生产降级+Error 日志、Development 直接抛错；`BuiltInToolSchemaTests` 在 CI 兜底 |
| 会话工作区 | 五个 workspace 工具操作宿主侧 session-\<key\>/work（bind-mount 进会话沙箱 /work，与 execute_code 共享状态）：`list_workspace_files`（ReadOnly）/`read_workspace_file`（cat -n 行号 + offset/limit 分页，ReadOnly）/`write_workspace_file`/`edit_workspace_file`（精确字符串替换 + 唯一性校验）/`export_workspace_file`（工作区→file asset→publish 交付桥）；`download_file` 可选 workspacePath 直落工作区 |
| 计划工具 | `update_plan`（对标 Claude Code TodoWrite / Codex update_plan）：全量重发步骤列表，≤1 个 in_progress；结果快照落进会话时间线，SSE 附加 `plan_updated` 事件 |
| 最大轮次控制 | 默认 50 轮（`AgentConfig.MaxTurns`，fallback 同为 `DefaultMaxTurns=50`） |

## Architecture
```text
RuntimePreparation
  ├─ CapabilityToolFactory: 授权过滤 → AITool（schema 校验/降级）
  ├─ IAgentFeatureFactory → McpFeatureFactory / SkillFeatureFactory: 能力定义与上下文
  └─ IsolatedToolFunction.Wrap(超时, 分级预算, 并发类别, 独占信号量, logger)
       │  导出为 RuntimeTool（Name / Description / ParametersJsonSchema / InvokeAsync）
         ├─ ToolResult(IsError) → 错误信封原样回传
         ├─ 成功内容 → 预算截断（头尾保留 + 收窄提示）
         ├─ Exclusive → 每轮共享信号量串行（排队计入超时）；ReadOnly → 并发执行
         ├─ 超时 → {"error","code":"tool_timeout","timedOut":true}（排队超时文案可区分）
         └─ 异常 → 脱敏信封（errorId 关联日志）
```

发现阶段执行可见性授权；执行阶段再次校验权限，避免发现与调用之间权限变化。

工具描述按开发指南维护：写明何时用/何时不用/失败怎么办/与其他工具的配合（如 `write_file` 明确不能修改已有文件、`read_file` 明确二进制走 publish 路径）。

## Current Status
**Implemented** — 能力适配、结构化结果契约、统一错误信封、分级结果预算、异常脱敏已落地。

## Limits
- ReadOnly 并行白名单当前为内置读取类工具（含 workspace 读取）；未显式声明并发类别的工具一律串行
- 无工具调用结果缓存
- 预算按字符计（≈4 字符/token），非精确 token 计数
- 16 个内置工具全开时定义体量 ≈ 11.3k 字符（~2.8k tokens/请求）；按环境/代理关功能开关（FileAssets、CodeExecution、Rag）收敛
- 外部 MCP 工具不由平台计数与预算，其目录规模由 harness 侧决定

## Source
- Application：`Backend/src/OpenAgent.Application/Tooling/Discovery/`（CapabilityToolFactory）、`Backend/src/OpenAgent.Application/Tooling/Invocation/`（隔离、预算与并发策略）、`Backend/src/OpenAgent.Application/Execution/RuntimePreparation.cs`（工具导出与再授权）、`Backend/src/OpenAgent.Application/Capabilities/Skill/`（Skill 适配）。
- Capabilities：`Backend/src/OpenAgent.Capabilities/Plan/PlanCapabilitySource.cs` 以及 `Files/Tools/`、`Rag/`、`Code/`、`Workspace/`、`UserProfile/` 中的能力源；调用授权位于 `Backend/src/OpenAgent.Capabilities/Security/`。
- 共享能力定义：`Backend/src/OpenAgent.Contracts/Capabilities/CapabilityDefinition.cs`、`ICapabilitySource.cs`、`ToolConcurrency.cs`；业务能力不引用模型或 MCP SDK。
- Contracts: `Backend/src/OpenAgent.Contracts/Capabilities/ToolResult.cs`、`Backend/src/OpenAgent.Contracts/Runtime/IAgentRuntimeDriver.cs`（`RuntimeTool`）
- Tests: `Backend/tests/OpenAgent.Application.Tests/Tooling/Discovery/CapabilityToolFactoryTests.cs`、`Tooling/Discovery/BuiltInToolSchemaTests.cs`、`Tooling/Invocation/ToolResultBudgetTests.cs`、`Tooling/Invocation/ToolConcurrencyTests.cs`
