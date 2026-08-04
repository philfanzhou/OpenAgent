# Feature: MCP 客户端能力

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

## 相关文档

- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
