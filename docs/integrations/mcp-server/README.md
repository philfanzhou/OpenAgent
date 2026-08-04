
## Feature


## 集成目标

OpenAgent 通过官方 C# SDK 连接外部 MCP Server，将服务器工具映射为 Agent 可调用的工具，并支持读取服务器资源。当前默认 transport 为 Streamable HTTP，显式 `SSE` 配置用于兼容 legacy 服务。

## 组件清单

| 组件 | 路径 | 职责 |
|------|------|------|
| `IMcpClient` | `Agent.Contracts/Mcp/IMcpClient.cs` | 稳定契约 |
| `McpClient` | `src/Core/Capabilities/Mcp/McpClient.cs` | 对外 facade 与生命周期入口 |
| `McpConnection` | `src/Core/Capabilities/Mcp/McpConnection.cs` | SDK 会话建立、工具刷新与断开 |
| `McpTransportFactory` | `src/Core/Capabilities/Mcp/McpTransportFactory.cs` | 按 `McpServerType` 创建 SSE/Http transport |
| `McpToolCatalog` / `McpToolInvoker` | `src/Core/Capabilities/Mcp/` | 工具映射、缓存与调用 |
| `McpResourceReader` | `src/Core/Capabilities/Mcp/McpResourceReader.cs` | 文本和 Blob 资源映射 |
| `McpConnectionManager` | `src/Core/Execution/Tools/McpConnectionManager.cs` | 多服务器别名、类型和连接切换 |
| SDK 客户端 | `ModelContextProtocol.Core` 1.4.1 | 协议协商、请求、Streamable HTTP / SSE 生命周期 |
| 测试服务 | `tests/OpenAgent.TestFramework/Mocks/WireMockMcpServer.cs` | 官方 SDK SSE 服务端夹具 |

## 已实现能力

- 基于配置连接一个或多个 MCP Server。
- 自动发现工具并生成 `mcp_{server}_{tool}` 别名。
- 调用工具并将文本结果返回给 Agent engine。
- 映射标准破坏性工具提示。
- 读取文本和 Blob 资源。
- 按 `McpServerType.SSE` 或 `McpServerType.Http` 选择 legacy SSE 或 Streamable HTTP。
- 连接失败时记录服务器维度日志，不阻止其他服务器继续加载。

## 当前限制

- 已实现 Streamable HTTP 与 legacy SSE；`Stdio` 配置值尚不可用。
- 运行时每个 scoped 客户端只维持一个连接，多服务器调用时按绑定切换。
- `Http.Url` 必须填写服务器暴露的完整 MCP endpoint，客户端不会自动追加 `/mcp`。
- legacy SSE 服务器需按官方协议通过 SSE `message` 事件关联响应。
- MCP 注册表发布不会自动启用服务，必须将条目加入 Agent 配置并发布 Agent。

## Specification


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

## Design


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

## Tasks


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

## Tests


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

## Conventions


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
