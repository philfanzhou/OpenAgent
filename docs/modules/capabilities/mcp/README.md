# MCP Client

MCP 客户端负责连接外部 MCP 服务器、发现工具、执行工具以及读取资源。协议、JSON-RPC 和传输生命周期全部委托给官方 C# SDK `ModelContextProtocol.Core` 1.4.1。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 连接 | 官方 `HttpClientTransport`；默认 `StreamableHttp`，显式 `SSE` 时使用 legacy |
| 协议协商 | 官方 `McpClient.CreateAsync` 完成 initialize/initialized |
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
**Implemented** — 生产侧不解析 SSE event，也不组装 JSON-RPC 消息。

## Limits
- `McpServerType.Stdio` 仍是配置枚举，生产客户端未实现
- 不建立跨请求连接池；一次请求内每个 Server 复用一个客户端
- `Http` 类型的 URL 必须是完整 MCP endpoint，不自动追加 `/mcp`

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Mcp/`（McpCapabilitySource、McpServerClient）
- Contracts: `Backend/src/OpenAgent.Contracts/Mcp/IMcpClient.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/McpCapabilitySourceTests.cs`
