# Feature: MCP Server 集成

## 集成目标

OpenAgent 通过官方 C# SDK 连接外部 MCP Server，将服务器工具映射为 Agent 可调用的工具，并支持读取服务器资源。当前默认 transport 为 Streamable HTTP，显式 `SSE` 配置用于兼容 legacy 服务。

## 组件清单

| 组件 | 路径 | 职责 |
|------|------|------|
| `IMcpClient` | `Agent.Contracts/Mcp/IMcpClient.cs` | 稳定契约 |
| `McpClient` | `src/Core/Capabilities/Mcp/McpClient.cs` | 对外 facade 与生命周期入口 |
| `McpConnection` | `src/Core/Capabilities/Mcp/McpConnection.cs` | SDK 会话建立、工具刷新与断开 |
| `McpTransportFactory` | `src/Core/Capabilities/Mcp/McpTransportFactory.cs` | 按 `McpServerType` 创建 SSE/Http transport |
| `McpToolCatalog` / `McpToolInvoker` | `src/Core/Capabilities/Mcp/` | 工具映射、缓存与调用 |
| `McpResourceReader` | `src/Core/Capabilities/Mcp/McpResourceReader.cs` | 文本和 Blob 资源映射 |
| `McpConnectionManager` | `src/Core/Execution/Tools/McpConnectionManager.cs` | 多服务器别名、类型和连接切换 |
| SDK 客户端 | `ModelContextProtocol.Core` 1.4.1 | 协议协商、请求、Streamable HTTP / SSE 生命周期 |
| 测试服务 | `tests/OpenAgent.TestFramework/Mocks/WireMockMcpServer.cs` | 官方 SDK SSE 服务端夹具 |

## 已实现能力

- 基于配置连接一个或多个 MCP Server。
- 自动发现工具并生成 `mcp_{server}_{tool}` 别名。
- 调用工具并将文本结果返回给 Agent engine。
- 映射标准破坏性工具提示。
- 读取文本和 Blob 资源。
- 按 `McpServerType.SSE` 或 `McpServerType.Http` 选择 legacy SSE 或 Streamable HTTP。
- 连接失败时记录服务器维度日志，不阻止其他服务器继续加载。

## 当前限制

- 已实现 Streamable HTTP 与 legacy SSE；`Stdio` 配置值尚不可用。
- 运行时每个 scoped 客户端只维持一个连接，多服务器调用时按绑定切换。
- `Http.Url` 必须填写服务器暴露的完整 MCP endpoint，客户端不会自动追加 `/mcp`。
- legacy SSE 服务器需按官方协议通过 SSE `message` 事件关联响应。
- MCP 注册表发布不会自动启用服务，必须将条目加入 Agent 配置并发布 Agent。

## 相关文档

- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
