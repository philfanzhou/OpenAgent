# MCP Server Integration

OpenAgent 通过官方 C# SDK 连接外部 MCP Server，将服务器工具映射为 Agent 可调用的工具，并支持读取服务器资源。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 连接管理 | 基于配置连接一个或多个 MCP Server |
| 工具发现与调用 | 自动发现工具并生成 `mcp_{server}_{tool}` 别名 |
| 资源读取 | 读取文本和 Blob 资源 |
| 传输选择 | 默认 Streamable HTTP，显式 `SSE` 兼容 legacy 服务 |
| 故障隔离 | 单服务器连接失败不阻止其他服务器加载 |

## Architecture
```text
AgentConfig.Mcp.Servers
        │
        ▼
ToolAssembler / McpToolExecutor
        │ McpConnectionManager.EnsureConnectedAsync
        ▼
McpClient facade
        │ McpConnection → McpTransportFactory
        ▼
ModelContextProtocol.Core 1.4.1
```

## Current Status
**Implemented** — 协议、JSON-RPC 和传输生命周期全部委托给官方 SDK。生产侧不再维护手写协议代码。

## Limits
- `Stdio` 配置值尚不可用
- 运行时每个 scoped 客户端只维持一个连接，多服务器按绑定切换
- `Http.Url` 必须为完整 MCP endpoint，客户端不自动追加 `/mcp`
- MCP 注册表发布不会自动启用服务，必须加入 Agent 配置并发布

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/`（McpClient, McpConnection, McpTransportFactory 等）
- Contracts: `Backend/src/OpenAgent.Contracts/Mcp/IMcpClient.cs`
