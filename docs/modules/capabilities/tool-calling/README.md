# Tool Calling

工具调用体系是模型与外部能力交互的统一机制；RAG/MCP 进入 `ChatOptions.Tools`，Agent Skills 由 MAF `AIContextProvider` 管理其渐进披露工具。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 统一工具集合 | 模型看到 RAG 与官方 MCP `AITool`，无需感知来源差异 |
| 原生 Function Calling | 引擎返回 ToolCall → ExecuteToolAsync → 继续推理 |
| 工具路由 | `search_knowledge_base`→RAG，`mcp_*`→官方 MCP，`load_skill` / `read_skill_resource`→MAF Skill Provider |
| 最大轮次控制 | 默认 50 轮（`AgentConfig.MaxTurns`） |

## Architecture
```text
AgentFactory
  ├─ CapabilityToolFactory: RAG 授权 → AIFunction
  ├─ McpToolFactory: official McpClientTool
  ├─ AgentSkillsProviderFactory: official AgentSkillsProvider
  └─ ChatClientAgent / FunctionInvokingChatClient
```

发现阶段执行可见性授权；执行阶段再次校验权限，避免发现与调用之间权限变化。

## Current Status
**Implemented** — 原生 Function Calling 已落地，工具路由完整。

## Limits
- 无并行工具调用（逐个串行执行）
- 无工具调用结果缓存

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/`（CapabilityToolFactory 等）
- Contracts: `Backend/src/OpenAgent.Contracts/`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/CapabilityToolFactoryTests.cs` 等
