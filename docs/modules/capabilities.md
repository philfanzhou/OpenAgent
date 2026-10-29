# Capabilities — 能力集成

本域按功能组织，不等同于单个程序集：独立业务能力位于 `OpenAgent.Capabilities`；MCP 与 Skill 适配位于 `OpenAgent.Application`；共享定义和目录接口位于 `OpenAgent.Contracts/Capabilities`。修改入口与依赖边界见 [开发指南](../overview/DevelopmentGuide.md)。

## 工具调用（Tool Calling）

工具调用体系是模型与外部能力交互的统一机制。平台把已授权的能力转换为 `RuntimeTool` 交给运行时驱动，外部 MCP server 则连同定义透传给 harness 原生连接。运行时边界见 [OpenCode 运行时](../integrations/opencode-runtime.md)。

```text
RuntimePreparation
  ├─ CapabilityToolFactory: 授权过滤 → AITool（schema 校验/降级）
  ├─ IAgentFeatureFactory → McpFeatureFactory / SkillFeatureFactory: 能力定义与上下文
  └─ IsolatedToolFunction.Wrap(超时, 分级预算, 并发类别, 独占信号量, logger)
       │  导出为 RuntimeTool（Name / Description / ParametersJsonSchema / InvokeAsync）
         ├─ ToolResult(IsError) → 错误信封原样回传
         ├─ 成功内容 → 预算截断（头尾保留 + 收窄提示）
         ├─ Exclusive → 每轮共享信号量串行（排队计入超时）；ReadOnly → 并发执行
         ├─ 超时 → {"error","code":"tool_timeout","timedOut":true}（排队超时文案可区分）
         └─ 异常 → 脱敏信封（errorId 关联日志）
```

关键行为：

- **统一工具集合**：模型看到平台能力与 skill 工具，无需感知来源差异；外部 MCP 工具由 harness 原生发现。错误与预算截断语义见 [执行错误处理](execution.md)。
- **结构化结果契约**：`CapabilityDefinition.Invoke` 返回 `ToolResult`（Content/IsError/IsTruncated/TruncationHint/Metadata）；错误走统一信封 `{"error","code","hint"}` 而不是抛异常。
- **调用再授权**：每次调用前重新查授权，避免发现与调用之间权限变化仍沿用旧授权。
- **并发控制**：能力源显式声明 `ReadOnly` 的工具可并发；其余工具由每轮共享信号量串行化，排队等待计入单次调用超时。
- **Schema 硬化**：内置工具 schema 全部封闭（`additionalProperties:false`、参数带 type、required 引用校验）；非法 schema 生产降级 + Error 日志、Development 直接抛错；`BuiltInToolSchemaTests` 在 CI 兜底。
- **计划工具**：`update_plan`（对标 Claude Code TodoWrite / Codex update_plan）：全量重发步骤列表，≤1 个 in_progress；结果快照落进会话时间线，SSE 附加 `plan_updated` 事件。
- **最大轮次控制**：默认 50 轮（`AgentConfig.MaxTurns`，fallback 同为 `DefaultMaxTurns=50`）。

工具路由：`search_knowledge_base`→RAG，`load_skill`/`read_skill_resource`/`read_skill_file`→Skill 能力。代码执行不再由平台工具提供：独立 Runner 沙箱已移除，模型经宿主放行的 OpenCode 原生工具（如 `bash`，见 [OpenCode 运行时](../integrations/opencode-runtime.md)）执行代码。

工具描述按开发指南维护：写明何时用/何时不用/失败怎么办/与其他工具的配合（如 `write_file` 明确不能修改已有文件、`read_file` 明确二进制走 publish 路径）。

边界与取舍：

- ReadOnly 并行白名单当前为内置读取类工具；未显式声明并发类别的工具一律串行。
- 无工具调用结果缓存；预算按字符计（≈4 字符/token）。
- 按环境/代理关功能开关（FileAssets、Rag）收敛内置工具定义体量。
- 外部 MCP 工具不由平台计数与预算，其目录规模由 harness 侧决定。

## Skill

OpenAgent 提供符合官方 Agent Skills 规范的 `SKILL.md` 包。Web 端支持上传 ZIP 或手动填写单文件 Markdown；两者都必须包含 `SKILL.md`。ZIP 在 OSS 中按目录文件对象保存，并由索引对象记录路径和哈希；执行时只 materialize 到请求级临时目录，不再重复解压 ZIP。

包布局以 `SKILL.md` 位于包根为准（OpenCode 原生发现按 `.opencode/skills/<name>/SKILL.md` 解析，`read_skill_file` 也以包根为基点）。上传时若 ZIP 里整包被一层顶层目录包裹（"压缩文件夹"的常见产物），该包裹目录会从所有相对路径中剥掉；包裹内外混有文件时拒绝上传。归一化之前入库的旧记录在物化、在线编辑与脚本清单读取时做同样的剥前缀处理，无需迁移。

```text
POST /skills/packages
        │
        ▼
PostgreSQL SkillDefinitions + MinIO/S3
        │  租户共享对象键 + SkillPackageStorageIndex
        ▼
AgentConfig.Skills → AgentSkillsProviderFactory
        │  租户范围对象文件 → request temp directory
        ▼
AgentSkillsRuntime（RuntimeSkill 目录 + 平台工具）
        │
        ▼
RuntimeInvocation.Skills → 驱动链接进本轮工作区供 harness 原生发现
```

- **目录与绑定**：PostgreSQL 保存租户范围的 Skill 元数据（Redis `skill:published:index` 等仅作派生缓存，数据库可用时不用 Redis-only 数据恢复目录）；`SkillsConfig` 只从当前 Agent 配置选择已启用 Skill，目录注册不产生绑定；创建 provider 前按 Agent、Skill 和用户 ACL 过滤；`RuntimePreparationScope` 释放 skill 资源并删除临时目录。
- **平台工具**：`read_skill_file` 读取包内文件，限制在物化包目录内。包内脚本没有平台执行路径：Skill 物化进工作区后由宿主放行的 OpenCode 原生工具（如 `bash`）直接运行，是否放行是宿主对 harness 进程的知情选择。
- **在线编辑**：`PUT /skills/{skillId}/source` 原地替换 `SKILL.md`，包内其余文件保持不变；frontmatter `name` 不允许改变（改名需重新上传），否则 400。

Skill 只允许本地持久化来源：数据库中的目录元数据和租户对象存储中的 ZIP/MD 展开文件。Skill 对象键使用 `files/tenants/{tenant-hash}/skill-packages/...`，不包含 `users/{user-hash}`；HTTP Endpoint Skill 已移除。

## MCP

平台维护 MCP server 的配置目录与授权边界；server 的实际连接与工具调用由 harness 经 ACP 透传完成（详见 [MCP Server 集成](../integrations/mcp-server.md)与 [OpenCode 运行时](../integrations/opencode-runtime.md)）。

```text
MCP 配置页 → PostgreSQL openagent.mcp_configurations
        │
AgentConfig.Mcp.EnabledServerIds
        │
        ▼
McpFeatureFactory → RuntimeMcpServer[]（租户过滤、去重、逐 server 授权）
        │ 随 ACP session/new 透传
        ▼
harness 原生连接外部 MCP server
```

- 配置目录：MCP Server 独立维护并持久化到 PostgreSQL 租户注册表（`openagent.mcp_configurations`），使用 Server 名称作为绑定 ID。
- Agent 绑定：Agent 只保存 `EnabledServerIds`；运行时按 ID 经 `IMcpConfigRepository` 解析定义，内联 `Mcp.Servers` 仅作存量兼容。
- 权限：平台只对自有工具做逐调用授权；透传 server 的凭据与限流由 harness 持有。
- 故障隔离：解析失败或未授权的 server 被跳过，不影响其余 server 透传。
- 平台不代理外部 MCP 连接，也不复制 MCP 协议或工具执行逻辑。

## RAG

RAG 业务实现位于 Capabilities，经 Application 适配为模型可调用的工具参与执行，是否被调用由模型和工具链路共同决定。多实例检索、适配器扩展（`IRagAdapter`）与基于用户上下文的 ACL 权限过滤均已落地。

| Adapter | Type | Index | Search |
|---------|------|-------|--------|
| QdrantAdapter | `qdrant` | 是（需嵌入模型） | 是 |
| RagFlowAdapter | `ragflow` | 否 | 是 |

```text
Agent → ToolCall("search_knowledge_base")
  → RagCapabilitySource
  → IRagService.SearchAsync/SearchDetailedAsync
  → IRagAdapter → RAG Backend
```

边界：Qdrant 索引请求中向量字段使用空数组占位，实际使用需调用嵌入模型；RagFlowAdapter 不支持索引；适配器响应解析使用同步 `.GetAwaiter().GetResult()`；无检索结果缓存。

## 代码执行

平台不再自带代码执行链路：独立 Runner（Bubblewrap 沙箱）、`execute_code` 工具、workspace 文件工具与 `run_skill_script` 已随 Runner 一并移除。代码执行交给 OpenCode 自身——模型使用宿主经 `OpenCode:AllowedNativeTools` 显式放行的原生工具（如 `bash`/`read`/`edit`/`write`）在轮次工作区内执行；放行即把对应主机读写/执行面直接交给 harness 进程（应用层授权，非 OS 沙箱），默认保持全拒绝，详见 [OpenCode 运行时](../integrations/opencode-runtime.md)。

## 代码入口

- 工具适配与隔离：`Backend/src/OpenAgent.Application/Tooling/Discovery/`（CapabilityToolFactory）、`Tooling/Invocation/`（隔离、预算与并发策略）、`Execution/RuntimePreparation.cs`。
- Skill：`Backend/src/OpenAgent.Contracts/Capabilities/ISkillCatalog.cs`、`OpenAgent.Engine/Config/SkillCatalogStore.cs`、`OpenAgent.Application/Capabilities/Skill/`（AgentSkillsProviderFactory）、`OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`。
- MCP：`Backend/src/OpenAgent.Application/Capabilities/Mcp/McpFeatureFactory.cs`、`Contracts/Configuration/IMcpConfigRepository.cs`、`Framework/Configuration/McpConfigRepository.cs`。
- RAG：`Backend/src/OpenAgent.Capabilities/Rag/`（RagCapabilitySource、RagService、Adapters/）、`Contracts/Models/IRagAdapter.cs`。
- 计划工具与内置能力源：`Backend/src/OpenAgent.Capabilities/Plan/` 以及 `Files/Tools/`、`UserProfile/`。
- 共享契约：`Backend/src/OpenAgent.Contracts/Capabilities/`（CapabilityDefinition、ICapabilitySource、ToolResult、ToolConcurrency）、`Contracts/Runtime/IAgentRuntimeDriver.cs`（RuntimeTool）。
- 测试：`Backend/tests/OpenAgent.Application.Tests/Tooling/`（Discovery、Invocation）、`Capabilities/Skill/`、`Backend/tests/OpenAgent.Capabilities.Tests/`（Rag、Security）、`Backend/tests/OpenAgent.Engine.Tests/Skills/`。
