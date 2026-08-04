# Architecture: MCP 客户端

## 组件边界

```text
Service / SkillProvider
        │ IMcpClient
        ▼
OpenAgent McpClient facade
  ├─ McpConnection / McpSessionState：SDK 会话生命周期
  ├─ McpTransportFactory：SSE/Streamable HTTP endpoint
  ├─ McpToolCatalog / McpToolInvoker：工具映射与调用
  └─ McpResourceReader：资源映射
        │
        ▼
ModelContextProtocol.Core 1.4.1
  ├─ McpClient：协议协商与 MCP 请求
  └─ HttpClientTransport：Streamable HTTP / legacy SSE、请求关联与重连
```

OpenAgent 代码不解析 SSE event，也不组装 JSON-RPC 消息。协议版本由客户端与服务器在初始化阶段协商。

## 连接流程

1. `ConnectAsync` 串行进入 `_connectionLock`。
2. 已有活动连接时直接返回；失效连接先释放。
3. 按 `McpServerType` 解析 endpoint：`Http` 直接使用完整配置 URL，`SSE` 兼容模式确保 `/sse` 只追加一次。
4. 创建 `HttpClientTransport`，设置对应 transport mode、5 秒连接超时、最多 5 次重连和 2 秒默认重连间隔。
5. `McpClient.CreateAsync` 执行官方初始化流程，初始化超时为 30 秒。
6. 调用 SDK `ListToolsAsync`，映射并缓存工具。
7. SDK `Completion` 未完成时，`IsConnected` 为 `true`。

## 工具执行流程

```text
别名解析 → 切换到工具所属服务器 → 检查缓存工具
       → SDK CallToolAsync → 提取 TextContentBlock
       → 根据 IsError 返回普通结果或错误字符串
```

`McpProtocolException` 保留官方错误码；取消异常在调用方取消时原样抛出。日志只记录业务事件和安全处理后的参数/结果，SDK 内部传输日志由注入的 `ILoggerFactory` 处理。

## 资源读取

- `TextResourceContents` 转为 UTF-8 `MemoryStream`。
- `BlobResourceContents` 使用 SDK 解码后的字节创建只读 `MemoryStream`。
- 空内容或未知内容类型抛出 `InvalidOperationException`。

## 断开与释放

`DisconnectAsync` 清空当前客户端、URL 和工具缓存，再异步释放官方 SDK 客户端；SDK 客户端负责释放它接管的 transport。仅在客户端创建失败时，适配层直接释放尚未被接管的 transport。`Dispose` 和 `DisposeAsync` 共用同一释放路径，并通过原子状态保证幂等。

## 故障定位

| 症状 | 优先检查 |
|------|----------|
| HTTP 返回 404 | `Http.Url` 是否准确指向完整 endpoint；客户端不会自动追加 `/mcp` |
| 无法连接 | URL 是否为绝对地址，配置 `Type` 是否与服务器 transport 一致 |
| SSE 初始化超时 | `/sse` 是否返回 endpoint event，POST 响应是否通过 SSE message 回送 |
| 工具为空 | 服务器 `tools/list` 能力与响应是否合法 |
| 工具调用错误 | `McpProtocolException` 错误码、服务器 `isError` 和 SDK 日志 |
| 连接中断 | 网络状态及 SDK transport 重连日志 |

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
