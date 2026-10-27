# MCP Client

平台维护 MCP server 的配置目录与授权边界；server 的实际连接与工具调用由 harness 经 ACP 透传完成。
实现边界见 [OpenCode 运行时](../../../integrations/opencode-runtime.md)。

## Core Capabilities

| Capability | Description |
|---|---|
| 配置目录 | MCP Server 独立维护并持久化到 PostgreSQL 租户注册表（`openagent.mcp_configurations`），使用 Server 名称作为绑定 ID |
| Agent 绑定 | Agent 只保存 `EnabledServerIds`；运行时按 ID 经 `IMcpConfigRepository` 解析定义 |
| 运行时透传 | `McpFeatureFactory` 解析出启用中的 server 定义，随 ACP `session/new` 交给 harness 原生连接 |
| 权限 | 平台只对自有工具做逐调用授权；透传 server 的凭据与限流由 harness 持有 |
| 故障隔离 | 解析失败或未授权的 server 被跳过，不影响其余 server 透传 |

## Architecture

```text
MCP 配置页 → PostgreSQL openagent.mcp_configurations
        │
AgentConfig.Mcp.EnabledServerIds
        │
        ▼
McpFeatureFactory → RuntimeMcpServer[]
        │ 随 ACP session/new 透传
        ▼
harness 原生连接外部 MCP server
```

平台不代理外部 MCP 连接，也不复制 MCP 协议或工具执行逻辑。旧版 `Mcp.Servers` 仍作为迁移兼容字段读取，
新配置不再把 endpoint 复制到 Agent。

## Source

- Application: `Backend/src/OpenAgent.Application/Capabilities/Mcp/McpFeatureFactory.cs`
- Contracts: `Backend/src/OpenAgent.Contracts/Configuration/IMcpConfigRepository.cs`
- Framework: `Backend/src/OpenAgent.Framework/Configuration/McpConfigRepository.cs`
