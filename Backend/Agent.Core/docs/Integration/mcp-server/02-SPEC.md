# Spec: MCP Server 集成

## 依赖

```xml
<PackageReference Include="ModelContextProtocol.Core" Version="1.4.1" />
```

SDK 提供 MCP 协议模型、客户端、SSE transport、协议协商和错误类型。OpenAgent 不固定 `ProtocolVersion`，由 SDK 与服务器协商双方支持的版本。

## Agent 配置

```json
{
  "Mcp": {
    "Servers": [
      {
        "Name": "weather",
        "Url": "http://localhost:3001/custom/mcp",
        "Type": "Http"
      }
    ]
  }
}
```

| 字段 | 必填 | 规则 |
|------|------|------|
| `Name` | 是 | 用于日志和工具别名前缀 |
| `Url` | 是 | `Http` 时为完整 MCP endpoint；`SSE` 时为基础 URL 或 `/sse` URL |
| `Type` | 否 | 默认 `Http`；可显式设为 `SSE`；`Stdio` 尚未实现 |

## Transport 配置

| SDK 选项 | 当前值 |
|----------|--------|
| `Endpoint` | `Http` 使用完整 `Url`；`SSE` 兼容补齐 `/sse` |
| `TransportMode` | `HttpTransportMode.StreamableHttp` 或显式 `HttpTransportMode.Sse` |
| `ConnectionTimeout` | 5 秒 |
| `MaxReconnectionAttempts` | 5 |
| `DefaultReconnectionInterval` | 2 秒 |
| `InitializationTimeout` | 30 秒 |
| `ClientInfo` | `OpenAgent` / `1.0.0` |

## 服务器兼容要求

Streamable HTTP 服务器必须在配置 URL 上实现官方单 endpoint 语义，客户端直接向该地址发送初始化和后续 MCP 请求。endpoint 可以是根路径、`/mcp` 或自定义路径。

显式配置为 `SSE` 时，服务器必须实现官方 legacy SSE transport：

1. SSE endpoint 接受 GET 并发送 `endpoint` event。
2. 客户端把 MCP 请求 POST 到 endpoint event 指定的地址。
3. 对有 ID 的请求，响应通过原 SSE 会话发送 `message` event。
4. 支持 SDK 初始化协商和 `tools/list`；使用资源时还需支持 `resources/read`。

两种模式的帧格式均由 SDK 处理，OpenAgent 代码不依赖具体 JSON-RPC 字段布局。

## 失败语义

- 连接或初始化失败：`ConnectionException`。
- 官方协议错误：工具调用返回含错误码的兼容错误字符串。
- 服务端 `CallToolResult.IsError=true`：返回 `Error executing tool ...`。
- 调用方取消：传播 `OperationCanceledException`。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
