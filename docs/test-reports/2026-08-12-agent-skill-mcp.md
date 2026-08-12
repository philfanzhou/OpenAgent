# Agent 挂载 Skill 与 MCP 测试报告

**执行日期：** 2026-08-12
**分支：** `feat/agent-skill-mcp-pr`
**结论：** Skill 多格式解析、对象存储写入/读取、当前 Agent 挂载、MCP 多协议版本配置与协商结果、前端完整配置均已实现并通过对应自动化测试。应用内浏览器在当前会话不可用，因此真实 UI 截图未能采集；没有使用独立 Playwright 或伪造截图替代。

## 验收范围

| 场景 | 实现与验证 | 结果 |
|---|---|---|
| 当前 Agent 挂载 Skill | `SkillsConfig` 只向当前 Agent 暴露启用实例；对象包发现后形成 `CapabilityDefinition` 并调用 HTTP Endpoint | 通过 |
| Skill 多格式 | JSON、YAML、Markdown front matter、ZIP 内 manifest | 4 种格式通过 |
| Skill 对象存储 | 上传 API 写原始包；发现和“验证”操作重新读取对象并校验 SHA-256 | 通过 |
| 当前 Agent 挂载 MCP | `McpCapabilitySource` 仅遍历当前 `AgentConfig.Mcp.Servers` | 通过 |
| MCP 多协议版本 | 可自动协商或固定四个 SDK 支持版本；测试 API返回 requested/negotiated version | 4 个版本配置通过 |
| MCP 传输 | Streamable HTTP、legacy SSE、受白名单限制的 Stdio 保持可配置；本轮不启动任何 Stdio 子进程 | 通过静态与单元验证 |
| 配置即时生效 | 保存成功后驱逐该 Agent 的 `ConfigSnapshot`，下一次执行重新加载 | 单元测试通过 |
| 前端配置 | Skill 包上传/格式/对象键/摘要/验证/删除；MCP 传输/版本/地址/Stdio 参数与环境变量/连接测试 | 测试及生产构建通过 |

## Skill 包格式

| 文件 | Manifest 位置 | 关键字段 |
|---|---|---|
| `.json` | 文件本身 | `id`、`name`、`endpointUrl`、`parametersJsonSchema` |
| `.yaml` / `.yml` | 文件本身 | 同上，顶层标量 |
| `.md` / `.markdown` | YAML front matter | 同上；正文可作为 description |
| `.zip` | `skill.json`、`skill.yaml`、`skill.yml` 或 `SKILL.md` | 按内部文件格式解析 |

上传端点为 `POST /api/v1/admin/skills/{agentId}/packages`。原始字节写入 `IFileObjectStore`；Agent 配置只保存对象键、文件名、格式和 SHA-256。能力发现时 `ObjectStoredSkillProvider` 重新读取对象、校验摘要并解析 manifest，因此存取均通过对象存储完成。

## MCP 协议版本

`McpServerConfig.ProtocolVersion` 留空时由官方 SDK 自动协商；非空时传入 `McpClientOptions.ProtocolVersion`，服务端返回不匹配版本会使握手失败。SDK 1.4.1 支持：

- `2024-11-05`
- `2025-03-26`
- `2025-06-18`
- `2025-11-25`

连接测试响应同时返回 `requestedProtocolVersion` 与 `negotiatedProtocolVersion`。版本配置依据[官方 C# SDK 1.4.1 源码](https://github.com/modelcontextprotocol/csharp-sdk/blob/v1.4.1/src/ModelContextProtocol.Core/McpSessionHandler.cs)；HTTP 后续请求携带协商版本的要求见 [MCP 2025-06-18 changelog](https://modelcontextprotocol.io/specification/2025-06-18/changelog)。

## 自动化结果

| 命令 | 结果 |
|---|---|
| `dotnet build Backend/OpenAgent.sln --no-restore` | 通过，0 warning、0 error |
| Core Skill/MCP 过滤测试 | 21/21 通过；Core 全量 74/74 |
| Engine Skill 对象存储管理与配置即时生效测试 | 4/4 通过 |
| `pnpm test` | 8/8 通过 |
| `pnpm build` | 通过，Vue 类型检查和 Vite 生产构建成功 |
| `git diff --check` | 通过 |

完整解决方案测试中，Contracts 5、Core 70、Hosting 12、Architecture 6、Router 56 全部通过。另有以下与本次变更无关的环境/基线项：

- Engine 58/59：`CreateAgentRequest_NoChatContext_ExternalContextNull` 在当前 SDK 下得到自动生成的 `TraceIdentifier`，与测试的 `null` 预期不一致。
- Infrastructure 2/5：其余 3 项需要 Docker Testcontainers；本机 Docker endpoint `npipe://./pipe/docker_engine` 不可用。

## UI 截图

按 Browser 技能连接 `http://127.0.0.1:5173/` 时，浏览器运行时返回 `No browser is available`，随后检查可用浏览器列表为 `[]`。技能规定此时不得改用独立 Playwright 或其他浏览器控制面，因此本报告不附伪造或非同源截图。

浏览器可用后应补采两张图：

1. “Skill 绑定”页：显示四格式上传入口、对象存储格式标签，以及编辑框内 object key 和 SHA-256。
2. “MCP 绑定”页：显示传输、协议版本列，以及连接测试返回的实际协商版本。

## 关键实现

- `Backend/src/OpenAgent.Core/Capabilities/Skill/SkillPackageReader.cs`
- `Backend/src/OpenAgent.Core/Capabilities/Skill/ObjectStoredSkillProvider.cs`
- `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`
- `Backend/src/OpenAgent.Core/Capabilities/Mcp/McpServerClient.cs`
- `Backend/src/OpenAgent.Engine/Config/AgentConfigManagementService.cs`
- `Frontend/OpenAgent.Chat/src/App.vue`
