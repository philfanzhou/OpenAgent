# Tool Calling

工具调用体系是模型与外部能力交互的统一机制，不同来源（Skill / MCP / RAG）在工具调用层保持一致的语义。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 统一工具集合 | 模型看到 `ToolDefinition` 列表，无需感知来源差异 |
| 原生 Function Calling | 引擎返回 ToolCall → ExecuteToolAsync → 继续推理 |
| XML 降级 | `TryExtractToolUse` 解析 `<tool_use>` XML 标签 |
| 工具路由 | `search_knowledge_base`→RAG, `mcp_*`→MCP, 其余→Skill |
| 最大轮次控制 | 默认 5 轮（`AgentConfig.MaxTurns`） |

## Architecture
```text
MAF AIContextProvider
  → ToolAssembler: Skill / RAG / MCP 发现 + 授权
  → MafToolAdapter: CapabilityFunction → AIFunction
  → FunctionInvokingChatClient: 原生工具循环
  → ToolCallDispatcher: 执行授权 + 审计
  → Skill / RAG / MCP
```

发现阶段执行可见性授权；执行阶段再次校验权限，避免发现与调用之间权限变化。

## Current Status
**Implemented** — 原生 Function Calling 和 XML 降级均已落地，工具路由完整。

## Limits
- XML 降级模式下工具结果以 `user` 角色消息追加（非标准 `tool` 角色）
- 无并行工具调用（逐个串行执行）
- 无工具调用结果缓存

## Source
- Core: `src/Core/Capabilities/`（ToolAssembler、MafToolAdapter、ToolCallDispatcher）
- Contracts: `Agent.Contracts/`（ToolDefinition、McpToolIdentity）
- Tests: `test/OpenAgent.Core.Tests/`（工具收集、执行路由、MCP 别名、XML 降级）
