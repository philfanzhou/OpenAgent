# Data Models: MCP

## 配置模型

`Agent.Contracts/Configuration/AgentConfig.cs` 定义 MCP 配置。

| 类型/字段 | 类型 | 说明 |
|-----------|------|------|
| `McpConfig.Servers` | `List<McpServerConfig>` | Agent 可用的 MCP 服务器 |
| `McpServerConfig.Name` | `string` | 服务器显示名和工具别名前缀 |
| `McpServerConfig.Url` | `string` | `Http` 时为完整 MCP endpoint；`SSE` 时为基础 URL 或 `/sse` URL |
| `McpServerConfig.Type` | `McpServerType` | 默认 `Http`；`SSE` 为兼容模式；`Stdio` 尚未实现 |

配置兼容字符串数组和对象数组；字符串形式会推导服务器名称并默认使用 Streamable HTTP。

## OpenAgent 契约模型

`Agent.Contracts/Mcp/IMcpClient.cs` 中的 `McpTool` 是对 SDK 工具模型的稳定映射。

| 字段 | 来源 | 说明 |
|------|------|------|
| `Name` | `McpClientTool.Name` | 工具名称 |
| `Description` | `McpClientTool.Description` | 工具说明 |
| `Schema` | `McpClientTool.JsonSchema.GetRawText()` | JSON Schema 原文 |
| `IsDangerous` | `ProtocolTool.Annotations.DestructiveHint` | 标准破坏性提示 |

## SDK 内容模型映射

| SDK 类型 | OpenAgent 返回类型 |
|----------|--------------|
| `CallToolResult` | 首个 `TextContentBlock.Text` 或序列化结果 |
| `TextResourceContents` | UTF-8 `Stream` |
| `BlobResourceContents` | 解码后的二进制 `Stream` |
| `McpProtocolException` | 含官方错误码的错误字符串（工具调用） |
| 初始化/连接异常 | `ConnectionException` |

## 生命周期状态

适配层只持有一个活动 `ModelContextProtocol.Client.McpClient`；连接成功后，SDK 客户端接管 `HttpClientTransport` 生命周期。`IsConnected` 由 SDK 客户端存在且 `Completion` 未完成决定，不再保存 `_messagesEndpoint`、pending request 或手写重连计数。上层 `McpConnectionManager` 以 `(ServerUrl, McpServerType)` 作为活动连接身份，同 URL 切换 transport 时会重连。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
