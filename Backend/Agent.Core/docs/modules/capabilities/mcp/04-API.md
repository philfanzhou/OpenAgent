# API: MCP 客户端

## 公共接口

接口定义于 `Agent.Contracts/Mcp/IMcpClient.cs`。

```csharp
public interface IMcpClient
{
    Task ConnectAsync(string serverUrl, McpServerType type = McpServerType.Http,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> CallToolAsync(string toolName, Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default);
    Task<Stream> ReadResourceAsync(string resourceUri,
        CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}
```

## 方法语义

| 方法 | 前置条件 | 结果 |
|------|----------|------|
| `ConnectAsync` | 绝对 HTTP(S) URL；类型与服务端一致 | 建立 SDK HTTP 会话并刷新工具缓存 |
| `DisconnectAsync` | 无 | 幂等断开并清空缓存 |
| `ListToolsAsync` | 通常先连接 | 返回工具缓存副本 |
| `CallToolAsync` | 已连接且工具存在 | 返回文本结果或兼容现有调用方的错误字符串 |
| `ReadResourceAsync` | 已连接 | 返回文本或 Blob 流 |

所有异步方法均接受并传播 `CancellationToken`。调用方取消不会被转换成普通错误字符串。

## DI 注册

`src/Core/Exten/ServiceExtensions.cs` 注册：

```csharp
services.AddScoped<IMcpClient, McpClient>();
```

HTTP 连接由 `IHttpClientFactory` 创建，SDK 日志使用容器中的 `ILoggerFactory`。已删除旧的 `ConnectionPool` 注册；多服务器路由由 `McpConnectionManager` 的工具绑定和按需连接切换承担。

## Endpoint 规则

- `Type=Http`：URL 是完整 MCP endpoint，`https://host/`、`https://host/mcp`、`https://host/custom/path` 均按原值使用。
- `Type=SSE`：基础 URL 追加 `/sse`；已以 `/sse` 结尾时保持不变。
- 客户端不会为 `Http` 自动追加 `/mcp`，也不会通过额外请求猜测路径。
- URL 非绝对地址时，连接前抛出 URI 错误。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
