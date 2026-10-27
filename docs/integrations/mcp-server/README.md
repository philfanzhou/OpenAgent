# MCP Server Integration

OpenAgent 维护外部 MCP Server 的配置目录与 Agent 绑定关系；server 的实际连接与工具调用由
harness（OpenCode）经 ACP `session/new` 原生完成，平台不建立外部 MCP 客户端。
透传模型与安全取舍见 [OpenCode 运行时](opencode-runtime.md)，配置解析见 [MCP Client](../modules/capabilities/mcp/README.md)。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 配置目录 | MCP Server 在独立配置页维护，持久化到 PostgreSQL `openagent.mcp_configurations`，按 `(TenantId, Name)` 唯一 |
| Agent 绑定 | Agent 只保存 `EnabledServerIds`；运行时按 ID 从租户注册表解析定义，内联 `Mcp.Servers` 仅作存量兼容 |
| 运行时透传 | `McpFeatureFactory` 解析、去重并逐 server 授权后，定义随 ACP `session/new` 交给 harness 原生连接 |
| 授权边界 | 平台只做 server 级可用性授权；透传 server 的凭据与限流由 harness 持有，工具调用不经平台桥 |
| 故障隔离 | 解析失败或未授权的 server 被跳过，不影响其余 server 透传 |

## Architecture
```text
MCP 配置页 → PostgreSQL openagent.mcp_configurations（IMcpConfigRepository）
        │
AgentConfig.Mcp.EnabledServerIds
        │
        ▼
McpFeatureFactory → RuntimeMcpServer[]（租户过滤、去重、逐 server 授权）
        │ 随 ACP session/new 透传
        ▼
harness（OpenCode）原生连接外部 MCP server
```

## Current Status
**Implemented** — 配置目录、绑定解析与 ACP 透传已落地。平台侧 MCP 客户端（McpClientPool、McpToolFactory、 <!-- docguard-allow -->
`read_mcp_resource`）与 `mcp__{server}__{tool}` 工具名已删除，外部工具由 harness 以原生命名暴露。 <!-- docguard-allow -->

## Limits
- 当前 server 定义仅 URL/类型，鉴权头为后续项；凭据由 harness 进程直接持有
- 逐调用授权、工具超时与结果预算只覆盖平台自有工具，不覆盖透传 server 的工具
- harness 与 server 之间的网络路径不受平台管控（见 OpenCode 运行时「MCP 透传」）
- 仅远程 HTTP/SSE server；本地 Stdio 进程不在本集成范围

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Configuration/IMcpConfigRepository.cs`
- Framework: `Backend/src/OpenAgent.Framework/Configuration/McpConfigRepository.cs`
- Application: `Backend/src/OpenAgent.Application/Capabilities/Mcp/McpFeatureFactory.cs`
