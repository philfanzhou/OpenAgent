# Design: MCP Server 集成

## 设计原则

协议实现归官方 SDK，OpenAgent 只保留领域适配。这样协议版本、JSON-RPC 请求关联、SSE/Streamable HTTP 传输和重连策略随 SDK 演进，不再由业务代码复制维护。

## 连接设计

```text
AgentConfig.Mcp.Servers
        │
        ▼
ToolAssembler / McpToolExecutor
        │ McpConnectionManager.EnsureConnectedAsync
        ▼
McpClient facade
        │ McpConnection → McpTransportFactory
        │ HttpClientTransport(Sse | StreamableHttp)
        ▼
external MCP Server
```

连接成功后立即通过 SDK 拉取工具列表。`ToolAssembler` 将原始工具名绑定到服务器 URL 和 transport 类型，并生成全局别名；执行工具时先切换到绑定服务器，再使用原始名称调用。

## 多服务器设计

`IMcpClient` 为 scoped，单实例只有一个活动 SDK 会话。配置多个服务器时：

1. 加载阶段依次连接每台服务器并缓存其工具绑定。
2. 每个工具别名保存 `ServerUrl`、`McpServerType` 与原始 `ToolName`。
3. 调用阶段若当前 URL 或 transport 类型不同，先断开再连接目标服务器。
4. 某台服务器失败只跳过该服务器，其他服务器继续加载。

旧 `ConnectionPool` 未参与实际路由，已删除，避免产生双重生命周期所有权。

## 协议所有权

| 责任 | 所有者 |
|------|--------|
| initialize/initialized | 官方 `McpClient` |
| JSON-RPC 序列化和 request ID | 官方 SDK |
| Streamable HTTP 与 SSE endpoint/message 解析 | `HttpClientTransport` |
| 重连 | `HttpClientTransport` |
| 服务器配置和工具别名 | OpenAgent `McpConnectionManager` |
| transport endpoint 选择 | OpenAgent `McpTransportFactory` |
| SDK 模型到 OpenAgent 契约 | `McpToolCatalog` / `McpResourceReader` |
| 业务日志和安全摘要 | OpenAgent observability helpers |

## 生命周期

连接、断开由 `SemaphoreSlim` 串行化。失败连接会释放尚未被接管的临时 transport；成功创建后由 SDK client 接管 transport，正常断开只释放 SDK client。同步和异步 Dispose 使用同一路径并保证幂等。

## Endpoint 与注册表边界

Streamable HTTP 是默认模式，配置 URL 被视为完整 endpoint，不进行 `/mcp` 猜测。legacy SSE 只能通过显式 `Type=SSE` 启用。Redis MCP 注册表不参与 Engine 运行时解析；`AgentConfig.Mcp.Servers` 是唯一事实源，以避免注册表发布绕过 Agent 级作用域。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
