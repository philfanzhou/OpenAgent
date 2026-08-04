# Feature: 工具调用统一规则与执行循环

## 用户故事

作为执行内核，我希望不同来源的能力（Skill、MCP、RAG）在工具调用层面保持一致的语义，以便模型看到统一的工具集合并可靠地执行工具调用。

## 概述

工具调用体系是 Agent.Core 中模型与外部能力交互的统一机制。不同来源的能力在工具调用层面保持一致的语义，由 Service 根据工具名称判断来源并路由执行。

## 工具来源

| 来源 | 标识规则 | 说明 |
|------|---------|------|
| Core 内部工具 | 原始名称 | 通过 IToolRegistry 注册 |
| 本地 Skill | 原始名称 | 实现 ISkill 接口 |
| MCP 外部工具 | `mcp_{server}_{tool}` 前缀 | 通过 IMcpClient 发现 |
| RAG 检索 | `search_knowledge_base` 固定名称 | 通过 RagSearchTool 实现 |

## 核心能力

- 统一工具集合：模型看到的是 ToolDefinition 列表，无需感知来源差异
- 原生 Function Calling：引擎返回 ToolCall → ExecuteToolAsync → 继续推理
- XML 降级：TryExtractToolUse 解析 `<tool_use>` XML 标签
- 工具路由：search_knowledge_base → RAG, mcp_* → MCP, 其余 → Skill
- 最大轮次控制：默认 5（可配置）

## 当前状态

**已实现** — 原生 Function Calling 和 XML 降级均已落地，工具路由完整。

## 当前限制

- XML 降级模式下工具结果以 user 角色消息追加（非标准 tool 角色）
- 无并行工具调用执行（逐个串行执行 ToolCalls）
- 无工具调用结果缓存

## 相关文档

- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
