# Tasks: MCP Server 集成

## 已完成

- [x] 引入官方 `ModelContextProtocol.Core` 1.4.1。
- [x] 使用 `HttpClientTransport` 的 Streamable HTTP 与 legacy SSE 模式建立连接。
- [x] 由 SDK 完成协议协商、JSON-RPC 和 SSE 生命周期。
- [x] 使用 SDK `ListToolsAsync`、`CallToolAsync`、`ReadResourceAsync`。
- [x] 映射标准 `annotations.destructiveHint`。
- [x] 删除手写 request ID、pending request、SSE parser 和 `RpcException`。
- [x] 删除未使用的 `ConnectionPool`。
- [x] 按 `McpServerType` 选择 transport mode；Http 使用完整配置 endpoint，SSE 兼容补齐 `/sse`。
- [x] 在官方 SDK 适配层内拆分连接、transport、工具目录、工具调用和资源读取职责。
- [x] 保留安全的 MCP 工具调用日志与错误字符串契约。
- [x] 使用官方 `ModelContextProtocol.AspNetCore` 构建 SSE 集成测试服务。
- [x] 覆盖工具发现、工具调用、资源读取和 Engine MCP 流程。
- [x] 支持根路径、`/mcp` 与任意自定义 Streamable HTTP endpoint。
- [x] 连接复用同时比较服务器 URL 与 transport 类型。

## 后续任务

- [ ] 增加 Blob 资源专项测试。
- [ ] 增加 SDK 断线重连和取消传播专项测试。
- [ ] 如业务需要，实现官方 SDK Stdio transport。
- [ ] 增加真实外部 MCP Server 的非 CI E2E 验证。

## 变更要求

后续协议能力必须优先升级或配置官方 SDK，不能在 `McpClient` 中恢复手写 JSON-RPC/SSE 逻辑。涉及 transport 变更时至少验证 Core solution 和 `TestCode/TestEnv.sln`。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
