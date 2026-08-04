# Conventions: MCP Server 集成

## 依赖约定

- 生产客户端依赖官方 `ModelContextProtocol.Core`。
- ASP.NET Core 测试服务器依赖官方 `ModelContextProtocol.AspNetCore`。
- 两个包保持同一固定版本；本次版本为 1.4.1。
- 不引入第二套 MCP 协议库，也不在业务层复制 SDK 内部实现。

## Server 配置约定

- `Name` 使用稳定、可读的短名，参与工具别名和日志。
- `Url` 不包含凭据，必须是绝对 HTTP(S) 地址。
- `Type=Http` 时 `Url` 是完整 endpoint，支持根路径、`/mcp` 和自定义路径，不自动追加 `/mcp`。
- `Type=SSE` 时基础 URL 与 `/sse` URL 均可，客户端保证 `/sse` 只出现一次。
- `Http` 是默认类型；`Stdio` 不代表已实现。

## 工具约定

- 服务器工具名保持原样，Agent 暴露名使用 `mcp_{server}_{tool}` 规范化别名。
- Input Schema 必须是有效 JSON Schema，并由 SDK 模型原样传递。
- 破坏性操作使用 MCP 标准 `annotations.destructiveHint`，不使用自定义字段。
- 服务端可预期错误使用 `CallToolResult.IsError`，协议错误使用官方错误类型。

## Transport 约定

- Streamable HTTP 与 SSE/POST 细节由官方 SDK transport 负责。
- legacy SSE 服务端必须通过 SSE `message` 返回有 ID 请求的结果，不能只写 POST response body。
- 重连、请求关联和协议版本协商不在 OpenAgent 代码中二次实现。
- transport 切换须保留当前 `IMcpClient` 取消、错误和释放语义。

## 安全与日志

- 禁止在 URL 中配置 token；认证应通过受控 HttpClient 配置扩展。
- 工具参数和结果只记录安全摘要，不能记录完整敏感内容。
- SDK transport 日志沿用应用 `ILoggerFactory`，避免重复打印协议流水账。
- 连接失败日志必须包含服务器名或 URL 上下文，但不得包含凭据。

## 测试约定

- 协议级测试优先组合官方 SDK 客户端和服务端。
- 测试服务 API 可保持业务友好的 `SetupTool`，内部实现必须符合官方 transport。
- SDK 升级需同时运行 Core 单测和 TestEnv 集成测试。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
