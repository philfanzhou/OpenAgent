# Conventions: MCP 客户端

## SDK 使用约定

- MCP 协议、JSON-RPC、SSE 解析、请求关联和协议版本协商必须使用官方 `ModelContextProtocol` SDK。
- 禁止在业务代码新增手写 `jsonrpc` payload、request ID、pending request 字典或 SSE 行解析器。
- SDK 版本在 `.csproj` 显式固定；升级时同时构建 Core 和 `TestCode/TestEnv.sln`。
- `McpServerType.Http` 使用 `HttpTransportMode.StreamableHttp`；显式 `SSE` 使用 `HttpTransportMode.Sse`。

## URL 与连接约定

- 输入 URL 去除首尾空白。
- `Http` URL 是完整 endpoint，保留自定义路径并禁止自动追加 `/mcp`。
- `SSE` URL 仅在末尾不是 `/sse` 时追加 `/sse`。
- 一个 `McpClient` 同时持有一个活动 SDK 会话。
- `McpConnectionManager` 按 URL 与 transport 类型共同判断连接复用。
- 连接和断开通过 `_connectionLock` 串行化。
- 连接超时 5 秒，初始化超时 30 秒，SDK 最大重连 5 次，默认重连间隔 2 秒。

## 映射约定

- 工具 Schema 必须使用 SDK `JsonSchema` 原文，不能重新生成。
- 危险工具只认标准 `annotations.destructiveHint`。
- 工具错误保持既有字符串语义，供上层 `IsToolErrorResult` 判断。
- 资源只显式支持 SDK 的 `TextResourceContents` 与 `BlobResourceContents`。

## 错误与取消

- 用户取消必须原样传播 `OperationCanceledException`。
- 初始化和连接失败包装为内部 `ConnectionException`。
- `McpProtocolException` 记录官方错误码，不定义重复的 `RpcException`。
- 未连接读取资源和未知资源内容类型使用 `InvalidOperationException`。

## 日志与安全

- 业务层保留连接、断开、资源读取和工具调用事件日志。
- 参数和结果通过 `ToolCallLog` 的安全摘要记录，禁止输出密钥或完整敏感 payload。
- transport 细节交给 SDK 的 `ILoggerFactory`，不复制逐行 SSE 流水日志。

## 释放约定

- 连接成功后只释放 SDK `McpClient`，由它释放已接管的 `HttpClientTransport`。
- 客户端创建失败时，适配层释放尚未被接管的 transport；transport 拥有并释放当前 `HttpClient`。
- `Dispose`、`DisposeAsync` 和 `DisconnectAsync` 必须幂等并清空工具缓存。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
