# MCP Client

OpenAgent 使用官方 `ModelContextProtocol.Core` C# SDK 连接外部 MCP Server，并把 SDK 返回的 `McpClientTool` 直接交给 MAF Agent。

## Core Capabilities

| Capability | Description |
|---|---|
| 传输 | 官方 `HttpClientTransport`、SSE 和受策略约束的 Stdio |
| 协议协商 | 官方 `McpClient.CreateAsync` 完成初始化与版本协商 |
| 工具发现 | `ListToolsAsync` 返回官方 `McpClientTool` |
| Agent 绑定 | MCP Server 只从当前 Agent 配置加载 |
| 故障隔离 | 单服务器连接失败不阻止其他服务器加载 |

## Architecture

```text
AgentConfig.Mcp.Servers
        │
        ▼
McpToolFactory
        │ official McpClient + ListToolsAsync
        ▼
McpClientTool.WithName(...)
        │
        ▼
ChatClientAgent.ChatOptions.Tools
```

平台只保留 Agent 配置、权限、传输策略和请求级资源生命周期，不复制 MCP 协议或工具执行逻辑。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/McpToolFactory.cs`, `McpTransportFactory.cs`
- Tests: official SDK option/configuration coverage and integration tests
