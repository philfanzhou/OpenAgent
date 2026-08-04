# Tests: MCP Server 集成

## 单元测试

`Agent.Core/test/OpenAgent.Core.Tests/Mcp/McpClientSdkTests.cs` 通过官方客户端真实发起请求，验证 OpenAgent 适配结果。

| 场景 | 预期 |
|------|------|
| Streamable HTTP 根路径、`/mcp`、自定义路径 | 按配置 endpoint 连接并发现工具 |
| SSE 初始化和 tools/list | 连接成功且工具缓存可用 |
| 标准 destructive annotation | `McpTool.IsDangerous=true` |
| tools/call 文本内容 | 返回原文本 |
| resources/read 文本内容 | 返回 UTF-8 流 |
| transport endpoint 映射 | SSE 兼容补齐 `/sse`，Http 保留完整配置 endpoint |
| 多服务器绑定 transport 类型 | 调用时向 `IMcpClient` 传递对应 `McpServerType` |

`SdkSseMessageHandler` 只提供确定性的 HTTP/SSE 边界，不替代生产协议客户端。

`McpHttpServerFixture` 使用官方 `ModelContextProtocol.AspNetCore` 启动真实 Streamable HTTP 服务，并通过 `MapMcp(route)` 验证自定义 endpoint。

## 集成测试

`tests/OpenAgent.TestFramework/Mocks/WireMockMcpServer.cs` 使用官方 `ModelContextProtocol.AspNetCore`：

- `AddMcpServer` 注册服务器信息和 handlers。
- `WithHttpTransport` 启用官方 legacy SSE 测试夹具。
- `WithListToolsHandler` 与 `WithCallToolHandler` 提供测试工具。
- `MapMcp` 映射 `/sse`、`/message` 及 SDK 端点。

`TestCode/Agent.TestEngine/McpTests.cs` 覆盖单工具、多工具选择、服务端错误、调用计数、多次调用和流式 Agent 请求。

## 执行命令

```bash
dotnet build Agent.Core/OpenAgent.Core.sln
dotnet test Agent.Core/OpenAgent.Core.sln --no-build
dotnet build TestCode/TestEnv.sln
dotnet test TestCode/TestEnv.sln --no-build
```

## 验收标准

- 构建 0 error、0 warning。
- Core 测试无失败；依赖本地 Redis 的既有测试可保持 skip。
- TestEnv 集成测试无失败。
- `git diff --check` 无空白错误。
- 生产 `McpClient.cs` 不出现手写 `jsonrpc`、`notifications/initialized`、SSE `event:`/`data:` 解析代码。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
