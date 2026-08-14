# MCP Server Integration

OpenAgent 通过官方 C# SDK 连接外部 MCP Server，将服务器工具映射为 Agent 可调用的工具，并支持读取服务器资源。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 连接管理 | 基于配置连接一个或多个 MCP Server |
| 工具发现与调用 | 自动发现可用工具并生成 `mcp__{server}__{tool}` 运行时名称 |
| 资源读取 | 读取文本和 Blob 资源 |
| 传输选择 | 默认 Streamable HTTP，显式 `SSE` 兼容 legacy 服务 |
| 协议版本 | 自动协商或固定 SDK 支持的四个日期版本，并返回协商结果 |
| 故障隔离 | 单服务器连接失败不阻止其他服务器加载 |

## Architecture
```text
AgentConfig.Mcp.Servers
        │
        ▼
CapabilityToolFactory
        │
        ▼
McpCapabilitySource（请求级客户端生命周期）
        │
        ▼
McpServerClient
        ▼
ModelContextProtocol.Core 1.4.1
```

## Current Status
**Implemented** — MCP Server 必须绑定在具体 Agent 配置中并随 Agent 配置统一保存；协议、JSON-RPC 和传输生命周期全部委托给官方 SDK。生产侧不再维护手写协议代码。

## Limits
- `Stdio` 仅允许执行服务端策略白名单中的命令
- 不建立跨请求连接池；一次请求内每个 Server 复用一个客户端
- `Http.Url` 必须为完整 MCP endpoint，客户端不自动追加 `/mcp`
- MCP 注册表发布不会自动启用服务，必须加入 Agent 配置并发布

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/`（McpCapabilitySource、McpServerClient）
- Contracts: `Backend/src/OpenAgent.Contracts/Mcp/IMcpClient.cs`
