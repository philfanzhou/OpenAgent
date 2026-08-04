# Architecture: MAF 原生工具能力

`MafCapabilityProvider` 是工具能力进入 Agent 的唯一运行时入口。

```text
MAF AIContextProvider
  -> ToolAssembler: Skill / RAG / MCP discovery authorization
  -> MafToolAdapter: CapabilityFunction -> AIFunction
  -> FunctionInvokingChatClient: native tool loop
  -> ToolCallDispatcher: execute authorization + audit
  -> Skill / RAG / MCP
```

平台不实现模型/工具迭代，不合并流式 ToolCall，也不解析 XML 降级协议。
`FunctionInvokingChatClient` 负责函数调用、结果回填、循环终止和最大迭代次数。

发现阶段执行 Skill、MCP、Tool、Function 的可见性授权；`AIFunction` 执行体再次执行
execute 授权，避免发现与调用之间的权限变化。MCP runtime name 只用于避免同名冲突，
授权和审计仍使用原始 server/tool 身份。

工具名称、描述和 JSON Schema 只存在于 `AITool` 元数据，不再重复写入 system prompt。
