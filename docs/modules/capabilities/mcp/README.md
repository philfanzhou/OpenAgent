# MCP Client

MCP 客户端负责连接外部 MCP 服务器、发现工具、执行工具以及读取资源。协议、JSON-RPC 和传输生命周期全部委托给官方 C# SDK `ModelContextProtocol.Core` 1.4.1。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 连接 | 官方 `HttpClientTransport`；默认 `StreamableHttp`，显式 `SSE` 时使用 legacy |
| 协议协商 | 官方 `McpClient.CreateAsync` 完成 initialize/initialized |
| 版本控制 | 留空自动协商，或固定 `2024-11-05`、`2025-03-26`、`2025-06-18`、`2025-11-25` |
| 工具发现与调用 | `ListToolsAsync` / `CallToolAsync`，映射文本内容和 `IsError` |
| 资源读取 | `ReadResourceAsync`，支持文本和 Blob 内容 |
| 危险工具标记 | 读取标准 `annotations.destructiveHint` |

## Architecture
```text
CapabilityToolFactory
        │ ICapabilitySource
        ▼
McpCapabilitySource（请求级）
  ├─ 过滤不可用 Server
  ├─ 每个 Server 保持一个 McpServerClient
  └─ MCP Tool → CapabilityDefinition
        │
        ▼
McpServerClient
  └─ ModelContextProtocol.Core 1.4.1
```

## Current Status
**Implemented** — MCP Server 绑定属于 Agent 配置的一部分，由 `/api/v1/admin/agents/{agentId}/config` 统一保存；生产侧不解析 SSE event，也不组装 JSON-RPC 消息。连接测试返回请求版本和实际协商版本；请求级连接身份包含协议版本，避免跨版本复用。

## Limits
- `McpServerType.Stdio` 已实现；命令受 `Mcp:AllowedCommands` 策略限制（`McpTransportFactory.CreateStdioTransport`）
- 不建立跨请求连接池；一次请求内每个 Server 复用一个客户端
- `Http` 类型的 URL 必须是完整 MCP endpoint，不自动追加 `/mcp`

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/`（McpCapabilitySource、McpServerClient）
- Contracts: `Backend/src/OpenAgent.Contracts/Mcp/IMcpClient.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/McpCapabilitySourceTests.cs`, `McpServerClientTests.cs`
