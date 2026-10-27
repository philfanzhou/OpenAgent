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
- **会话工作区**：五个 workspace 工具操作宿主侧 session-&lt;key&gt;/work（bind-mount 进会话沙箱 /work，与 execute_code 共享状态）：`list_workspace_files`（ReadOnly）、`read_workspace_file`（cat -n 行号 + offset/limit 分页，ReadOnly）、`write_workspace_file`、`edit_workspace_file`（精确字符串替换 + 唯一性校验）、`export_workspace_file`（工作区→file asset→publish 交付桥）；`download_file` 可选 workspacePath 直落工作区。
- **计划工具**：`update_plan`（对标 Claude Code TodoWrite / Codex update_plan）：全量重发步骤列表，≤1 个 in_progress；结果快照落进会话时间线，SSE 附加 `plan_updated` 事件。
- **最大轮次控制**：默认 50 轮（`AgentConfig.MaxTurns`，fallback 同为 `DefaultMaxTurns=50`）。

工具路由：`search_knowledge_base`→RAG，`load_skill`/`read_skill_resource`/`run_skill_script`→Skill 能力，`execute_code`→隔离 Runner（经 `SkillScriptRunner`，需三层开关）。

工具描述按开发指南维护：写明何时用/何时不用/失败怎么办/与其他工具的配合（如 `write_file` 明确不能修改已有文件、`read_file` 明确二进制走 publish 路径）。

边界与取舍：

- ReadOnly 并行白名单当前为内置读取类工具（含 workspace 读取）；未显式声明并发类别的工具一律串行。
- 无工具调用结果缓存；预算按字符计（≈4 字符/token）。
- 16 个内置工具全开时定义体量 ≈ 11.3k 字符（~2.8k tokens/请求）；按环境/代理关功能开关（FileAssets、CodeExecution、Rag）收敛。
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
- **平台工具**：`read_skill_file` 读取包内文件；双开关 + 按实例开启后额外提供 `run_skill_script`。
- **在线编辑**：`PUT /skills/{skillId}/source` 原地替换 `SKILL.md`，包内脚本与脚本执行开关保持不变；frontmatter `name` 不允许改变（改名需重新上传），否则 400。

### 脚本执行安全边界

Skill 指令加载与资源读取默认启用；包内脚本默认完全不可执行。脚本执行需要三层开关同时开启：宿主 `CodeExecution.Enabled`（隔离 Runner 已部署）、Agent 配置的 `CodeExecution` 绑定、以及 `SkillInstanceConfig.ScriptExecutionEnabled`（按 Skill 实例，默认 false）。未全部开启时 `ScriptFilter` 不披露任何脚本，runner 显式拒绝执行。

上传的包被视为不可信代码：上传接口接受可选 multipart 字段 `scriptExecutionEnabled`，缺省为 false；即使显式传 true，包内没有可执行脚本（`.py`/`.js`/`.mjs`/`.sh`）也会被 400 拒绝。上传时扫描出的脚本相对路径记入 `ScriptNames` 随实例保存。重新上传（覆盖同名 Skill）会把开关重置为 false——新代码需要重新审阅开启。已上传的 Skill 通过 `PATCH /api/v1/admin/skills/{skillId}`（body `{ "scriptExecutionEnabled": bool }`）开启或关闭：服务端从对象存储重新读取包清单校验脚本存在，刷新清单后写回目录。该开关只管理披露面；运行时仍受三层开关、沙箱约束与授权复核限制。

开启后脚本也绝不在 Engine 进程内执行：`SkillScriptRunner` 将整个 Skill 包按相对路径全量挂载为沙箱 `/input` 输入（保留包内目录结构，Python 包根与脚本目录进入 `sys.path`，跨目录 import 可解析），经生成的专用 wrapper 入口（`.py` → `openagent_skill_entry__.py` 以 `runpy` 启动；`.js`/`.mjs` → `openagent_skill_entry__.mjs` 以动态 `import` 启动；`.sh` → `openagent_skill_entry__.sh` 以 `exec /bin/bash` 启动）在 Bubblewrap 沙箱内运行（无网络、非 root、固定 venv / Node / bash），并按会话复用 Runner 工作区；沙箱边界、限额与产物登记和 `execute_code` 完全一致（见下文代码执行）。参数以 JSON 传入 wrapper：Python 映射为 `sys.argv`、JavaScript 映射为 `process.argv`、shell 映射为位置参数。`.js` 以 CommonJS 语义加载，ESM 请使用 `.mjs`。`run_skill_script` 不经模型审批往返：当前 chat 契约无法承载审批请求，补偿控制即授权复核、扩展名与文件名白名单、共享预算和 Bubblewrap 隔离。

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

## 代码执行（CodeAct + Bubblewrap）

Engine.Host 组合 Application 的工具适配与 Capabilities 中的 `CodeCapabilitySource`，以 `AIFunction` 暴露 `execute_code`。模型生成 Python（默认）或 JavaScript，读取执行结果，并在运行时驱动的工具循环中修正代码。沙箱运行时支持 PPT、Excel、图表和 PDF 生成。本实现使用独立 Runner 和 Bubblewrap，原生运行在 Linux 主机，不依赖 Docker daemon、containerd、KVM 或 Hyperlight，也没有第二套 Agent 循环；Bubblewrap 的隔离参数由 Runner 固定生成，模型和请求均不能覆盖。Runner 安装、镜像构建与 Compose 部署见 [Code Runner 集成](../integrations/code-runner.md)。

必须同时启用 Engine `CodeExecution:Enabled` 与 Agent `config.codeExecution.enabled`；发现和执行工具均经过平台授权。Runner 的运行时、配额和宿主目录只接受管理员配置，不是模型参数。

```text
RuntimeAgentExecutor → RuntimePreparation → CapabilityToolFactory → execute_code
  → RunnerClient → authenticated Runner /api/v1/execute
    → BubblewrapCodeExecutor
      ├─ 无会话键: bwrap 一次性进程沙箱 → execute.py（prlimit 包装）
      └─ 会话键: SessionSandboxManager → 常驻 bwrap 沙箱 → supervisor.py（UDS 逐次执行）
  ← bounded logs + binary artifacts
  → FileAssetService.UploadAsync / EnsureReferencesAsync
  → model selects publish_files → assistant attachments
```

### 工具契约

`execute_code(code, inputFiles?, language?)` 的每个输入项为 `{fileId, name}`，文件在沙箱中位于 `/input/<name>`（`name` 允许 `/` 分隔的相对子路径，逐段校验）。输入必须属于当前租户、当前用户并已被当前会话引用；不可传对象存储键、宿主路径、环境变量或任意 Bubblewrap 参数。未指定自定义入口时 `main.py` 与 `main.mjs` 为保留名；`EntryFileName` 可将入口替换为其他同扩展名文件。

`language` 支持 `python`（默认，固定 venv，入口 `main.py`）和 `javascript`（固定 Node 运行时，ESM 入口 `main.mjs`，仅提供 Node 内置模块，没有 npm 包）。两种语言共享同一沙箱边界、资源限额和输入/输出协议。

返回 `executionId`、`exitCode`、`timedOut`、`stdout`、`stderr` 和文件元数据数组。成功文件登记为当前用户的 FileAsset 并关联当前会话；只有模型调用 `publish_files` 后才发布到 assistant 消息；二进制字节只在 Runner 与 Engine 之间传输，不进入模型上下文。产物入库不按类型过滤：任意扩展名均可登记，媒体类型按扩展名推断；仅空文件或超出存储层限制的产物被跳过并在 `skippedFiles` 中给出原因。输出只接受普通文件，拒绝符号链接、目录、特殊文件及危险名称。

会话语义：无 `SessionKey` 的调用每次创建全新的 namespace、tmpfs 工作区和解释器进程，退出即销毁。携带 `SessionKey`（当前为会话 ID）的调用复用常驻会话沙箱：supervisor（UDS 单连接单请求）串行执行每次调用，`/work`、`/tmp`、`/input` 的文件与 `pip install --user` 安装的包跨调用保留（`/work` 为宿主侧 `session-<key>/work` 的 bind-mount，与 workspace 文件工具共享状态）；`/output` 每次只返回新写入的文件。变量与后台进程不跨调用保留——每次调用结束即 kill 整个子进程组。空闲超过 `SessionIdleMinutes`（默认 120 分钟）后沙箱与工作区目录被回收；容量由 `MaxSessionSandboxes`（默认 64）限制，满时驱逐最久空闲的沙箱。继续编辑历史产物时，仍显式将前次返回的 fileId 作为新调用输入。`sandboxReset=true` 的语义是会话工作区状态丢失：沙箱进程死亡/驱逐后重建时宿主工作区仍存活，不报告 reset。

### 隔离边界

每次执行固定启用：

- 独立 user、PID、IPC、network、UTS namespace；cgroup namespace 在内核支持时启用。
- 沙箱 UID/GID 为 65532；非特权模式默认不保留 capabilities；禁止继续创建 user namespace，并创建新会话。
- 根文件系统从空 tmpfs 构造并整体重挂为只读，只读暴露 `/usr`、固定 Python venv 与 Node 运行时、最小 passwd/group 和字体配置。
- 一次性执行将请求输入只读挂载到 `/input`，tmpfs 退出即销毁；会话沙箱将请求输入经控制通道写入持久 `/input` tmpfs，仅含 supervisor socket 的通道目录以读写 bind 进沙箱（`/channel`）；`/output`、`/tmp` 仍为限额 tmpfs。
- 不挂载宿主 home、源码、服务配置、凭据、设备、Docker Socket、D-Bus socket 或网络。
- `--clearenv` 后只注入固定的解释器/locale/语言/时限变量（会话模式下语言/入口/时限按次由 supervisor 注入子进程）。
- 资源限制逐次施加：一次性执行由 `prlimit` 包装，会话执行由 supervisor `setrlimit`（地址空间、CPU 时间、进程数、打开文件数、单文件大小、core dump 等价）；Runner 并发及 systemd cgroup 再限制节点总量。
- 无宿主解释器回退；Bubblewrap、user namespace、Python venv 或 Node 运行时不可用时健康检查和执行均失败。

Runner 控制服务属于可信控制面，只允许 Engine 经私网和服务令牌访问，使用无登录、无 sudo、无 capabilities 的专用用户；systemd 进一步只开放工作目录写权限。Bubblewrap 与 Docker 一样共享宿主 Linux 内核，不能视为抵御未知内核漏洞的 MicroVM 强边界：适合受控用户或中等信任的代码执行；承载公开、恶意、多租户代码时应把 Runner 节点放进独立 VM 或改用 MicroVM 后端。沙箱返回的 JSON 同样是不可信输出，Runner 和 Engine 都校验协议大小、文件数量、文件名和字节限额；不能把脚本自行打印的"校验通过"当成隔离证明。

### 超时、取消与故障

沙箱包装脚本限制用户子进程墙钟时间，Runner 另有外部总截止时间。一次性执行的请求取消或外部截止时间到达时，Runner 终止 bwrap 进程树；会话沙箱只终止当次调用的子进程组（客户端断开即提前终止），沙箱本身存活，仅在外部截止（supervisor 失去响应）时销毁重建并在下一次调用报告 `sandboxReset`。`--die-with-parent` 确保 Runner 异常退出时全部沙箱同时退出；后台回收器按空闲期回收会话沙箱，并清扫 Runner 崩溃遗留且超过一小时的请求输入目录。

Engine 每请求默认最多执行 8 次代码，运行时驱动的 MaxTurns 继续约束模型循环。普通脚本错误通过 stderr 返回供模型修正；网络错误、Runner 不可用、超时和取消不会降级为宿主执行。Runner 请求目前与聊天请求共同存活，不提供断线后继续运行、进程重启后的 Agent 恢复或跨节点调度。

### 文档能力与验证

固定 Python venv 安装 python-pptx、openpyxl、XlsxWriter、pandas、matplotlib、Pillow 和 defusedxml；主机只读运行时提供 LibreOffice、中文字体和 Node.js。支持生成可编辑 Office 文件，也可在沙箱内调用 LibreOffice 渲染 PDF 后交付。Skill 包脚本的沙箱执行细节见上文 Skill 一节。

Capabilities 与 Application 测试分别覆盖独立能力和运行时集成，验证双重开关、授权撤销、跨租户/用户文件拒绝、二进制资产归属，以及确定性工具循环在错误后再次调用执行工具。Runner 的 Linux 真实测试验证 Bubblewrap 参数与实际边界、网络禁用、宿主文件不可见、任务隔离、内存限制、超时/取消清理、符号链接拒绝、输出截断、tmpfs 容量，以及 PPT/Excel 的生成、重新打开、再次编辑和 PPT 转 PDF，并覆盖 JavaScript 的 Node 执行与输入/输出文件往返。CI 显式安装 Bubblewrap、固定 Python venv 和 Node，随后发布 Runner 进行真实 HTTP 冒烟并执行部署脚本验收；其他平台会明确跳过真实沙箱测试。

## 代码入口

- 工具适配与隔离：`Backend/src/OpenAgent.Application/Tooling/Discovery/`（CapabilityToolFactory）、`Tooling/Invocation/`（隔离、预算与并发策略）、`Execution/RuntimePreparation.cs`。
- Skill：`Backend/src/OpenAgent.Contracts/Capabilities/ISkillCatalog.cs`、`OpenAgent.Engine/Config/SkillCatalogStore.cs`、`OpenAgent.Application/Capabilities/Skill/`（AgentSkillsProviderFactory、SkillScriptRunner）、`OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`。
- MCP：`Backend/src/OpenAgent.Application/Capabilities/Mcp/McpFeatureFactory.cs`、`Contracts/Configuration/IMcpConfigRepository.cs`、`Framework/Configuration/McpConfigRepository.cs`。
- RAG：`Backend/src/OpenAgent.Capabilities/Rag/`（RagCapabilitySource、RagService、Adapters/）、`Contracts/Models/IRagAdapter.cs`。
- 代码执行：`Backend/src/OpenAgent.Capabilities/Code/CodeCapabilitySource.cs`、`OpenAgent.Runner/`（BubblewrapCodeExecutor、SessionSandboxManager、sandbox/execute.py、supervisor.py）。
- 计划工具与内置能力源：`Backend/src/OpenAgent.Capabilities/Plan/` 以及 `Files/Tools/`、`Code/`、`Workspace/`、`UserProfile/`。
- 共享契约：`Backend/src/OpenAgent.Contracts/Capabilities/`（CapabilityDefinition、ICapabilitySource、ToolResult、ToolConcurrency）、`Contracts/Runtime/IAgentRuntimeDriver.cs`（RuntimeTool）。
- 测试：`Backend/tests/OpenAgent.Application.Tests/Tooling/`（Discovery、Invocation）、`Capabilities/Skill/`、`Backend/tests/OpenAgent.Capabilities.Tests/`（Rag、Security）、`Backend/tests/OpenAgent.Engine.Tests/Skills/`。
