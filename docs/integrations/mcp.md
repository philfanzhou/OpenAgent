# MCP Server Integration

OpenAgent 通过官方 C# SDK 连接外部 MCP Server，将服务器工具映射为 Agent 可调用的工具，并支持读取服务器资源。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 连接管理 | 基于配置连接一个或多个 MCP Server |
| 工具发现与调用 | 自动发现可用工具并生成 `mcp__{server}__{tool}` 运行时名称 |
| 资源读取 | 读取文本和 Blob 资源 |
| 传输选择 | 默认 Streamable HTTP，显式 `SSE` 兼容 legacy 服务 |
| 协议版本 | 自动协商或选择 SDK 支持的五个最低日期版本，并返回协商结果 |
| 故障隔离 | 单服务器连接失败不阻止其他服务器加载 |

## Architecture
```text
MCP 配置页 → Redis MCP registry → AgentConfig.Mcp.EnabledServerIds
        │
        ▼
McpToolFactory（请求级客户端生命周期）
        │
        ▼
官方 McpClient → McpClientTool
        ▼
ModelContextProtocol.Core 1.4.1
```

## Current Status
**Implemented** — MCP Server 在独立配置页维护并注册到 Redis，Agent 只绑定 Server ID；协议、JSON-RPC 和传输生命周期全部委托给官方 SDK。旧版内嵌 `Mcp.Servers` 仅用于兼容已有配置，生产侧不再维护手写协议代码。

## Limits
- 当前仅接入远程 HTTP / SSE MCP；本地 Stdio 进程执行不在本集成范围内
- 不建立跨请求连接池；一次请求内每个 Server 复用一个客户端
- `Http.Url` 必须为完整 MCP endpoint，客户端不自动追加 `/mcp`
- MCP 注册表发布不会自动启用服务，必须加入 Agent 配置并发布

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/`（McpToolFactory、McpTransportFactory）
