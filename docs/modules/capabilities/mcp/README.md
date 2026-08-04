
## Feature


## 功能概述

MCP 客户端负责连接外部 MCP 服务器、发现工具、执行工具以及读取资源。生产实现位于 `src/Core/Capabilities/Mcp/`，协议、JSON-RPC 和 transport 生命周期全部委托给官方 C# SDK `ModelContextProtocol.Core` 1.4.1。

## 核心能力

| 能力 | 当前实现 |
|------|----------|
| 连接 | 官方 `HttpClientTransport`；默认 `StreamableHttp`，显式 `SSE` 时使用 legacy `Sse` |
| 协议协商 | 官方 `McpClient.CreateAsync` 完成 initialize/initialized，不固定协议版本 |
| 工具发现 | `ListToolsAsync`，连接后缓存工具元数据 |
| 工具调用 | `CallToolAsync`，映射文本内容和 `IsError` |
| 资源读取 | `ReadResourceAsync`，支持文本和 Blob 内容 |
| 危险工具标记 | 读取标准 `annotations.destructiveHint` |
| 取消与释放 | 透传 `CancellationToken`，同步/异步释放 SDK 对象 |

## 当前状态

**已实现。** 生产侧不再维护 JSON-RPC 请求 ID、pending request、SSE 解析循环或自定义重连状态。SDK 负责协议与传输细节，OpenAgent 适配层只处理配置、契约映射、日志和错误语义。

## 当前限制

- `McpServerType.Stdio` 仍是配置枚举，生产客户端当前未实现 Stdio。
- 一个 scoped `IMcpClient` 同时只保持一个活动连接；多服务器由 `McpConnectionManager` 按工具绑定切换连接。
- `Http` 类型的 URL 必须是完整 MCP endpoint，可以是根路径、`/mcp` 或任意自定义路径；客户端不追加 `/mcp`。
- SDK 的 legacy SSE 模式仅用于兼容显式配置为 `SSE` 的现有服务器。
- MCP 注册表是管理端素材库；只有加入 Agent 的 `Mcp.Servers` 并发布 Agent 配置后才会生效。

## 关键源文件

- `src/Core/Capabilities/Mcp/McpClient.cs`
- `src/Core/Capabilities/Mcp/McpConnection.cs`
- `src/Core/Capabilities/Mcp/McpTransportFactory.cs`
- `src/Core/Capabilities/Mcp/McpToolInvoker.cs`
- `src/Core/Execution/Tools/McpConnectionManager.cs`
- `src/Core/Exten/ServiceExtensions.cs`
- `Agent.Contracts/Mcp/IMcpClient.cs`
- `test/OpenAgent.Core.Tests/Mcp/McpClientSdkTests.cs`

## Architecture


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

## Data Models


## 配置模型

`Agent.Contracts/Configuration/AgentConfig.cs` 定义 MCP 配置。

| 类型/字段 | 类型 | 说明 |
|-----------|------|------|
| `McpConfig.Servers` | `List<McpServerConfig>` | Agent 可用的 MCP 服务器 |
| `McpServerConfig.Name` | `string` | 服务器显示名和工具别名前缀 |
| `McpServerConfig.Url` | `string` | `Http` 时为完整 MCP endpoint；`SSE` 时为基础 URL 或 `/sse` URL |
| `McpServerConfig.Type` | `McpServerType` | 默认 `Http`；`SSE` 为兼容模式；`Stdio` 尚未实现 |

配置兼容字符串数组和对象数组；字符串形式会推导服务器名称并默认使用 Streamable HTTP。

## OpenAgent 契约模型

`Agent.Contracts/Mcp/IMcpClient.cs` 中的 `McpTool` 是对 SDK 工具模型的稳定映射。

| 字段 | 来源 | 说明 |
|------|------|------|
| `Name` | `McpClientTool.Name` | 工具名称 |
| `Description` | `McpClientTool.Description` | 工具说明 |
| `Schema` | `McpClientTool.JsonSchema.GetRawText()` | JSON Schema 原文 |
| `IsDangerous` | `ProtocolTool.Annotations.DestructiveHint` | 标准破坏性提示 |

## SDK 内容模型映射

| SDK 类型 | OpenAgent 返回类型 |
|----------|--------------|
| `CallToolResult` | 首个 `TextContentBlock.Text` 或序列化结果 |
| `TextResourceContents` | UTF-8 `Stream` |
| `BlobResourceContents` | 解码后的二进制 `Stream` |
| `McpProtocolException` | 含官方错误码的错误字符串（工具调用） |
| 初始化/连接异常 | `ConnectionException` |

## 生命周期状态

适配层只持有一个活动 `ModelContextProtocol.Client.McpClient`；连接成功后，SDK 客户端接管 `HttpClientTransport` 生命周期。`IsConnected` 由 SDK 客户端存在且 `Completion` 未完成决定，不再保存 `_messagesEndpoint`、pending request 或手写重连计数。上层 `McpConnectionManager` 以 `(ServerUrl, McpServerType)` 作为活动连接身份，同 URL 切换 transport 时会重连。

## API


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

## Tests


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

## Conventions


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
