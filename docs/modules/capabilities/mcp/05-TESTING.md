# Testing: MCP 客户端

## 测试层级

| 层级 | 位置 | 覆盖内容 |
|------|------|----------|
| 适配层 SDK 测试 | `test/OpenAgent.Core.Tests/Mcp/McpClientSdkTests.cs` | Streamable HTTP 路由、官方 SSE 握手、标准 annotations、工具调用、资源读取 |
| 官方 HTTP 服务夹具 | `test/OpenAgent.Core.Tests/Mcp/McpHttpServerFixture.cs` | 官方 `ModelContextProtocol.AspNetCore` 根路径、`/mcp` 和自定义路径 |
| HTTP 测试 handler | `test/OpenAgent.Core.Tests/Mcp/SdkSseMessageHandler.cs` | endpoint/message 事件和官方客户端请求序列 |
| 路由单元测试 | `test/OpenAgent.Core.Tests/Mcp/McpRoutingTests.cs` | 多服务器别名与按服务器切换 |
| 组件单元测试 | `test/OpenAgent.Core.Tests/Mcp/McpComponentTests.cs` | SSE/Http endpoint 映射、工具目录与资源内容映射 |
| Engine 集成测试 | `TestCode/Agent.TestEngine/McpTests.cs` | 完整 Agent 工具发现、选择、调用、错误和流式路径 |
| 官方服务端夹具 | `tests/OpenAgent.TestFramework/Mocks/WireMockMcpServer.cs` | `ModelContextProtocol.AspNetCore` legacy SSE transport |

## 关键断言

- 工具元数据从 `annotations.destructiveHint` 映射到 `McpTool.IsDangerous`。
- Streamable HTTP 直接请求配置 endpoint，不追加固定路径。
- SDK `CallToolAsync` 的文本内容保持原业务返回值。
- 文本资源按 UTF-8 返回流。
- 集成测试服务通过 SSE `message` 回送响应，而不是依赖 POST 响应体。
- 多服务器工具别名、调用计数、错误结果和 streaming Agent 流程保持可用。
- transport 类型随工具绑定传递，SSE 兼容补齐 `/sse`，Http 保留完整配置 endpoint。
- 同 URL 不同 transport 必须断开并重连。

## 推荐命令

```bash
dotnet test Agent.Core/OpenAgent.Core.sln
```

## 新增测试约定

- 不在测试中复制生产 JSON-RPC/SSE 解析器。
- 协议级集成优先使用官方 SDK 客户端/服务端组件。
- 需要精确构造边界响应时，可使用最小 `HttpMessageHandler`，但请求仍由官方客户端发出。
- 先观察失败，再修改实现；测试应断言外部行为，不反射 SDK 私有状态。

## 已知缺口

- 尚无真实外部 MCP 服务的自动化 E2E。
- Blob 资源、断线重连和服务端协议错误可继续补充专项测试。
- Streamable HTTP 已覆盖 endpoint/mode 映射，仍可补充确定性的完整握手测试。
- Stdio transport 尚未实现，因此没有对应测试。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
