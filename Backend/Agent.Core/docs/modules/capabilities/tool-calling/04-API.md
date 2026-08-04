# API — MAF 工具调用

## 发现

`ToolAssembler` 从 Skill、MCP 和 RAG 收集 `ToolDefinition`，执行 discover authorization，
并为同名 MCP 工具生成稳定且唯一的 runtime function name。

## MAF 函数

`MafToolAdapter` 把每个发现结果转换为 `AIFunction`：

- 名称、描述和 JSON Schema 来自发现快照；
- `AIFunction` 保存原始 `ToolDefinition`；
- MAF 的 `FunctionInvokingChatClient` 负责调用和结果回填；
- 执行体进入 `ToolCallDispatcher`，再次执行资源授权并调用具体能力。

平台不提供 `IAgentEngine` 或 `IAgentService` 工具回调 API，也不自己执行模型工具循环。

## Registry

`IToolRegistry` 仍用于平台运行时工具注册：

| 方法 | 说明 |
|---|---|
| `RegisterTool` | 注册 definition 和 executor |
| `GetTools` | 返回发现定义 |
| `ExecuteToolAsync` | 执行已注册工具 |
| `HasTool` | 检查名称 |

## 调用链

```text
AgentRun
  -> ChatClientAgent
  -> FunctionInvokingChatClient
  -> AIFunction
  -> ToolCallDispatcher
  -> Skill / MCP / RAG
```
