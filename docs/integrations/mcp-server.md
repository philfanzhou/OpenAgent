# MCP Server Integration

OpenAgent 维护外部 MCP Server 的配置目录与 Agent 绑定关系；聊天中的连接与工具调用由
harness（OpenCode）经 ACP `session/new` 原生完成。设置页另提供短生命周期的连接诊断，
仅握手和读取工具目录，不参与聊天执行。透传模型与安全取舍见 [OpenCode 运行时](opencode-runtime.md)，配置解析见 [MCP Client](../modules/capabilities.md)。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 配置目录 | MCP Server 在独立配置页维护，持久化到 PostgreSQL `openagent.mcp_configurations`，按 `(TenantId, Name)` 唯一 |
| Agent 绑定 | Agent 只保存 `EnabledServerIds`；运行时按 ID 从租户注册表解析定义，内联 `Mcp.Servers` 仅作存量兼容 |
| 运行时透传 | `McpFeatureFactory` 解析、去重并逐 server 授权后，定义随 ACP `session/new` 交给 harness 原生连接 |
| 授权边界 | 平台只做 server 级可用性授权；透传 server 的凭据与限流由 harness 持有，工具调用不经平台桥 |
| 故障隔离 | 解析失败或未授权的 server 被跳过，不影响其余 server 透传 |
| 设置页诊断 | 测试已保存或编辑中的 HTTP/SSE 定义，显示连接结果、服务端信息与工具 Schema，不保存配置、不执行工具 |

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
**Implemented** — 配置目录、绑定解析、ACP 透传和设置页连接诊断。聊天执行不保留平台侧 MCP 客户端池（McpClientPool）、McpToolFactory、
`read_mcp_resource` 与 `mcp__{server}__{tool}` 工具名；外部工具由 harness 以原生命名暴露。

## 设置页连接测试

在设置 → MCP 配置中点击某行的“测试连接”，或在编辑窗口中测试当前草稿。测试不要求先保存，也不改变 Agent 绑定。

- 接口：`POST /api/v1/admin/mcp/test-connection`，请求体沿用 `McpServerConfig`，管理端点的环境与认证限制不变。
- 使用官方 MCP SDK，按配置选择 Streamable HTTP 或 Legacy SSE，完成 `initialize` 后读取完整 `tools/list` 目录；不发起 `tools/call`。
- `protocolVersion` 留空时自动协商；指定时诊断要求服务端返回完全相同的版本，否则握手失败。此字段不改变聊天中 OpenCode 自身的协议协商。
- 返回 `success`、`connected`、`latencyMs`、服务端名称/版本、协商的协议版本、工具名称/标题/说明及输入/输出 JSON Schema。`connected` 表示握手成功，`success` 还要求工具目录读取成功；零工具属于成功，不等同于连接失败。
- 单次诊断最多等待 15 秒，结束后释放客户端；失败返回诊断错误，不回显上游异常、响应体或 URL 中的凭据。
- 诊断从 Engine 宿主发起，只证明该宿主当前能握手并读取目录，不保证工具可执行，也不替代 OpenCode 原生运行链路验收；现有配置仍不支持自定义鉴权头。

### 本地验证

2026-11-10：Engine 测试 259 通过、1 项既有 OpenCode 环境门控跳过；前端 `pnpm check` 的 195 项测试、类型检查与生产构建通过，包含设置重置时关闭编辑窗口、清除 MCP 诊断的回归。`ConfigurationEndpointTests.Mcp.cs` 使用真实 Kestrel 与官方 MCP SDK，覆盖 HTTP/SSE、目录分页、完整 Schema、空目录、握手/目录失败、错误脱敏、协议版本、认证、超时与取消。

实际浏览器使用隔离 PostgreSQL 和本地 MCP 服务验证了草稿测试不保存、已保存配置行测试、分页工具及输入/输出 Schema、零工具成功、HTTP 503 与目录读取失败；375px 下长工具名换行且无诊断窗口横向溢出。服务端记录只有握手通知与 `tools/list`，没有 `tools/call`。该验证不包含生产 MCP 或 OpenCode 聊天调用。

## Limits
- 当前 server 定义仅 URL/类型，鉴权头为后续项；凭据由 harness 进程直接持有
- 逐调用授权、工具超时与结果预算只覆盖平台自有工具，不覆盖透传 server 的工具
- harness 与 server 之间的网络路径不受平台管控（见 OpenCode 运行时「MCP 透传」）
- 仅远程 HTTP/SSE server；本地 Stdio 进程不在本集成范围

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Configuration/IMcpConfigRepository.cs`
- Framework: `Backend/src/OpenAgent.Framework/Configuration/McpConfigRepository.cs`
- Application: `Backend/src/OpenAgent.Application/Capabilities/Mcp/McpFeatureFactory.cs`
- 设置页诊断：`Backend/src/OpenAgent.Engine.Host/Endpoints/Admin/Mcp/`
- 前端：`Frontend/OpenAgent.Chat/src/features/mcp/`
