# MCP Client

OpenAgent 使用官方 `ModelContextProtocol.Core` C# SDK 连接外部 MCP Server，并把 SDK 返回的 `McpClientTool` 直接交给 MAF Agent。

## Core Capabilities

| Capability | Description |
|---|---|
| 传输 | 官方 `HttpClientTransport`、SSE 和受策略约束的 Stdio |
| 协议协商 | 官方 `McpClient.CreateAsync` 完成初始化与版本协商 |
| 工具发现 | `ListToolsAsync` 返回官方 `McpClientTool` |
| 配置目录 | MCP Server 独立维护并注册到 Redis，使用 Server 名称作为绑定 ID |
| Agent 绑定 | Agent 只保存 `EnabledServerIds`；运行时按 ID 从 MCP 注册表解析配置 |
| 故障隔离 | 单服务器连接失败不阻止其他服务器加载 |

## Architecture

```text
MCP 配置页 → Redis mcp:registry:{serverId}
        │
AgentConfig.Mcp.EnabledServerIds
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

平台只保留 MCP 配置目录、Agent 绑定关系、权限、传输策略和请求级资源生命周期，不复制 MCP 协议或工具执行逻辑。旧版 `Mcp.Servers` 仍作为迁移兼容字段读取，新配置不再把 endpoint、命令和密钥复制到 Agent。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/McpToolFactory.cs`, `McpTransportFactory.cs`
- Tests: official SDK option/configuration coverage and integration tests
