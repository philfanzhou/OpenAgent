# Capabilities — 能力集成

模型与外部能力交互的统一机制：工具调用循环、Skill、代码执行、MCP、RAG。RAG/MCP 进入 `ChatOptions.Tools`，Agent Skills 由 MAF `AIContextProvider` 管理其渐进披露工具。

## 工具调用

| Capability | Description |
|-----------|-------------|
| 统一工具集合 | 模型看到 RAG 与官方 MCP `AITool`，无需感知来源差异 |
| 原生 Function Calling | 引擎返回 ToolCall → ExecuteToolAsync → 继续推理 |
| 工具路由 | `search_knowledge_base`→RAG，`mcp_*`→官方 MCP，`load_skill` / `read_skill_resource`→MAF Skill Provider，`run_skill_script`→隔离 Runner（经 `SkillScriptRunner`，需三层开关） |
| 最大轮次控制 | 默认 50 轮（`AgentConfig.MaxTurns`） |

```text
AgentFactory
  ├─ CapabilityToolFactory: RAG 授权 -> AIFunction
  ├─ McpToolFactory: official McpClientTool
  ├─ AgentSkillsProviderFactory: official AgentSkillsProvider
  └─ ChatClientAgent / FunctionInvokingChatClient
```

发现阶段执行可见性授权；执行阶段再次校验权限，避免发现与调用之间权限变化。限制：无并行工具调用（逐个串行执行）、无工具调用结果缓存。

源码：`Backend/src/OpenAgent.Core/Capabilities/`（CapabilityToolFactory 等）；测试 `Backend/tests/OpenAgent.Core.Tests/Capabilities/CapabilityToolFactoryTests.cs`。

## Skill 技能

使用 MAF 官方 `AgentSkillsProvider`。Web 端支持上传 ZIP 或手动填写单文件 Markdown；两者都必须包含符合官方 Agent Skills 规范的 `SKILL.md`。ZIP 在 OSS 中按目录文件对象保存，并由索引对象记录路径和哈希；执行时只 materialize 到请求级临时目录，不再重复解压。

| Capability | Description |
|---|---|
| 官方格式 | `SKILL.md` YAML frontmatter + Markdown instructions |
| 渐进披露 | MAF 提供 `load_skill`、`read_skill_resource`，双开关 + 按实例开启后提供 `run_skill_script` |
| Skill 目录 | PostgreSQL 保存租户范围的 Skill 元数据；Redis `skill:published:index` + `skill:registry:{tenantHash}:{skillId}` 仅作派生缓存 |
| Agent 绑定 | `SkillsConfig` 只从当前 Agent 配置选择已启用 Skill；目录注册不产生绑定 |
| 权限过滤 | 在创建 provider 前按 Agent、Skill 和用户 ACL 过滤 |
| 生命周期 | `AgentExecutionScope` 释放 provider 并删除临时目录 |
| 脚本执行 | 默认禁用。宿主 `CodeExecution.Enabled` + Agent `CodeExecution` 绑定 + `SkillInstanceConfig.ScriptExecutionEnabled` 全部开启后，仅 `.py` 脚本经隔离 Runner 执行 |
| 脚本清单 | 上传时扫描包内 `.py` / `.js` / `.mjs` / `.sh` 文件，把相对路径记录到 `SkillInstanceConfig.ScriptNames`（`ScriptCount` 为派生值）；供管理端展示与开启前审阅 |
| 在线编辑 | `PUT /skills/{skillId}/source` 原地替换 `SKILL.md`，包内脚本与脚本执行开关保持不变 |

```text
POST /skills/packages
        |
        v
PostgreSQL SkillDefinitions + MinIO/S3
        |  租户共享对象键 + SkillPackageStorageIndex
        v
AgentConfig.Skills -> AgentSkillsProviderFactory
        |  租户范围对象文件 -> request temp directory
        v
MAF AgentSkillsProvider -> ChatClientAgent.AIContextProviders
```

安全边界：Skill 指令加载与资源读取默认启用；包内脚本默认完全不可执行。脚本执行需要三层开关同时开启：宿主 `CodeExecution.Enabled`（隔离 Runner 已部署）、Agent 配置的 `CodeExecution` 绑定、以及 `SkillInstanceConfig.ScriptExecutionEnabled`（按 Skill 实例，默认 false）。未全部开启时 `ScriptFilter` 不披露任何脚本，runner 显式拒绝执行。

上传与脚本执行配置：上传的包被视为不可信代码。上传接口（`POST /api/v1/admin/skills/packages` 与 `POST /api/v1/admin/skills/{agentId}/packages`）接受可选 multipart 字段 `scriptExecutionEnabled`，缺省 false；即使显式传 true，包内没有可执行脚本（`.py` / `.js` / `.mjs` / `.sh`）也会被 400 拒绝。重新上传（覆盖同名 Skill）会把开关重置为 false——新代码需要重新审阅开启。已上传的 Skill 通过 `PATCH /api/v1/admin/skills/{skillId}`（body `{ "scriptExecutionEnabled": bool }`）开启或关闭：服务端从对象存储重新读取包清单校验脚本存在（旧包没有 `ScriptNames` 记录也能正确处理），刷新清单后写回目录。Chat 前端在 Skill 目录表格中展示脚本数量与清单，并在开启时弹出信任确认；该开关只管理披露面，运行时仍受三层开关、沙箱约束与授权复核限制。

在线编辑：`PUT /api/v1/admin/skills/{skillId}/source`（body `{ "markdown": string }`）原地替换 `SKILL.md`：包内其余文件（含脚本）原样重存到新包，`ScriptExecutionEnabled` 与脚本清单保持不变——改描述不需要重新授权脚本。frontmatter `name` 不允许改变（改名需重新上传），否则 400。

开启后脚本也绝不在 Engine 进程内执行：`SkillScriptRunner` 将整个 Skill 包按相对路径全量挂载为沙箱 `/input` 输入（保留包内目录结构，Python 包根与脚本目录进入 `sys.path`，跨目录 import 可解析），经生成的专用 wrapper 入口（`.py` → `openagent_skill_entry__.py` 以 `runpy` 启动；`.js` / `.mjs` → `openagent_skill_entry__.mjs` 以动态 `import` 启动；`.sh` → `openagent_skill_entry__.sh` 以 `exec /bin/bash` 启动）在 Bubblewrap 沙箱内运行（无网络、非 root、固定 venv / Node / bash），并按会话复用 Runner 工作区（挂载输入跨调用保留，`/work`、`/output` 按调用隔离）。约束与 `execute_code` 完全一致：

- 仅 `.py`、`.js`、`.mjs`、`.sh` 脚本；其他解释器不披露、不执行；`execute_code` 工具的 schema 仍只声明 python / javascript，shell 目前仅经 skill 脚本通道使用。
- `ExecutionLimits` 输入文件上限（100 个、单文件 10 MiB、总 20 MiB、安全相对路径名）；包可自带 `main.py` / `main.mjs` / `main.sh`，wrapper 使用专用入口不与之冲突。
- 调用时按 `(Tool, run_skill_script)`、`(Function, run_skill_script)` 与 `(Skill, name)` 复核授权。
- 产物经 `FileAssetService` 登记为会话资产，由 `publish_files` 发布。
- 参数以 JSON 传入 wrapper：Python 映射为 `sys.argv`、JavaScript 映射为 `process.argv`、shell 映射为位置参数（单引号安全转义；字符串直传，其余 JSON 编码）。`.js` 以 CommonJS 语义加载，ESM 请使用 `.mjs`；`.sh` 以 bash 运行。

MAF 的 `run_skill_script` 审批回调被关闭（`DisableRunSkillScriptApproval`），原因是当前 chat 契约无法往返 MAF 审批请求；补偿控制即上述授权复核、扩展名与文件名白名单、共享预算和 Bubblewrap 隔离。

Skill 只允许本地持久化来源：数据库中的目录元数据和租户对象存储中的 ZIP/MD 展开文件。Skill 对象键使用 `files/tenants/{tenant-hash}/skill-packages/...`，不包含 `users/{user-hash}`；HTTP Endpoint Skill 已移除；Redis 不是事实源，数据库可用时不会用 Redis-only 数据恢复目录。

源码：Core `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs`、`SkillScriptRunner.cs`；Host `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`、`Extensions/ManagementEndpointExtensions.cs`；测试 `Backend/tests/OpenAgent.Core.Tests/Capabilities/AgentSkillPackageArchiveTests.cs`、`SkillScriptRunnerTests.cs`、`AgentSkillsProviderFactoryTests.cs`、`Backend/tests/OpenAgent.Engine.Tests/Skills/SkillPackageManagementServiceTests.cs`。

## 代码执行

Engine 通过 MAF `AIFunction` 暴露 `execute_code`。模型生成 Python（默认）或 JavaScript，读取执行结果，并在原有 MAF 工具循环中修正代码。沙箱运行时支持 PPT、Excel、图表和 PDF 生成。本实现使用独立 Runner 和 Bubblewrap，原生运行在 Linux 主机，不依赖 Docker daemon、containerd、KVM 或 Hyperlight，也没有添加第二套 Agent 循环。Bubblewrap 的隔离参数由 Runner 固定生成，模型和请求均不能覆盖。部署见 [code-runner](../integrations/code-runner.md)。

调用链：

```text
AgentExecutor -> AgentFactory -> CapabilityToolFactory -> execute_code
  -> RunnerClient -> authenticated Runner /v1/execute
    -> BubblewrapCodeExecutor
      ├─ 无会话键: bwrap 一次性进程沙箱 -> execute.py（prlimit 包装）
      └─ 会话键: SessionSandboxManager -> 常驻 bwrap 沙箱 -> supervisor.py（UDS 逐次执行）
  <- bounded logs + binary artifacts
  -> FileAssetService.UploadAsync / EnsureReferencesAsync
  -> model selects publish_files -> assistant attachments
```

实现入口：`Backend/src/OpenAgent.Core/Capabilities/Code/CodeCapabilitySource.cs`；`Backend/src/OpenAgent.Runner/BubblewrapCodeExecutor.cs`、`SessionSandboxManager.cs`、`SessionSandbox.cs`、`sandbox/execute.py`、`sandbox/supervisor.py`、`sandbox/execution_core.py`。

必须同时启用 Engine `CodeExecution:Enabled` 与 Agent `config.codeExecution.enabled`。发现和执行工具均经过平台授权。Runner 的运行时、配额和宿主目录只接受管理员配置，不是模型参数。

工具契约：`execute_code(code, inputFiles?, language?)` 的每个输入项为 `{fileId, name}`，文件在沙箱中位于 `/input/<name>`（`name` 允许 `/` 分隔的相对子路径，逐段校验）。输入必须属于当前租户、当前用户并已被当前会话引用；不可传对象存储键、宿主路径、环境变量或任意 Bubblewrap 参数。未指定自定义入口时 `main.py` 与 `main.mjs` 为保留名；`EntryFileName` 可将入口替换为其他同扩展名文件，此时仅保留新入口名。

`language` 支持 `python`（默认）和 `javascript`。JavaScript 由固定 Node 运行时执行，入口为 `main.mjs`（ESM），仅提供 Node 内置模块，没有 npm 包；Python 由固定 venv 执行，入口为 `main.py`。两种语言共享同一沙箱边界、资源限额和输入/输出协议。

返回 `executionId`、`exitCode`、`timedOut`、`stdout`、`stderr` 和文件元数据数组。成功文件登记为当前用户的 FileAsset，并关联当前会话；只有模型调用 `publish_files` 后才发布到 assistant 消息。二进制字节只在 Runner 与 Engine 之间传输，不进入模型上下文。

无 `SessionKey` 的调用每次创建全新的 namespace、tmpfs 工作区和解释器进程，退出即销毁。携带 `SessionKey`（当前为会话 ID）的调用复用一个常驻会话沙箱：沙箱内由可信 supervisor（UDS 单连接单请求）串行执行每次调用，`/work`、`/tmp`、`/input` 的文件与 `pip install --user` 安装的包跨调用保留；`/output` 中每次调用只返回新写入的文件（旧产物保留可读但不重复返回）。变量与后台进程不跨调用保留——每次调用结束即 kill 整个子进程组。空闲超过 `SessionIdleMinutes`（默认 120 分钟）后沙箱被回收；沙箱死亡或被回收后下一次调用自动重建全新沙箱，结果携带 `sandboxReset=true`。容量由 `MaxSessionSandboxes`（默认 64）限制，满时驱逐最久空闲的沙箱。继续编辑历史产物时，仍显式将前次返回的 fileId 作为新调用输入。输出只接受普通文件，拒绝符号链接、目录、特殊文件及危险名称。

隔离边界——每次执行固定启用：

- 独立 user、PID、IPC、network、UTS namespace；cgroup namespace 在内核支持时启用。
- 沙箱 UID/GID 为 65532；Bubblewrap 非特权模式默认不向沙箱进程保留 capabilities；同时禁止继续创建 user namespace，并创建新会话。
- 根文件系统从空 tmpfs 构造并整体重挂为只读，只读暴露 `/usr`、固定 Python venv 与 Node 运行时、最小 passwd/group 和字体配置。
- 一次性执行将请求输入只读挂载到 `/input`，tmpfs 退出即销毁；会话沙箱将请求输入经控制通道写入持久 `/input` tmpfs，并把仅含 supervisor socket 的通道目录以读写 bind 进沙箱（`/channel`），`/work`、`/output`、`/tmp` 同样为限额 tmpfs 但随沙箱存活。
- 不挂载宿主 home、源码、服务配置、凭据、设备、Docker Socket、D-Bus socket 或网络。
- `--clearenv` 后只注入固定的解释器/locale/语言/时限变量（会话模式下语言/入口/时限按次由 supervisor 注入子进程）。
- 资源限制逐次施加：一次性执行由 `prlimit` 包装，会话执行由 supervisor 对每次调用的子进程 `setrlimit`（地址空间、CPU 时间、进程数、打开文件数、单文件大小、core dump 等）；Runner 并发及 systemd cgroup 再限制节点总量。
- 无宿主解释器回退；Bubblewrap、user namespace、Python venv 或 Node 运行时不可用时健康检查和执行均失败。

Runner 控制服务属于可信控制面，只允许 Engine 经私网和服务令牌访问，并使用无登录、无 sudo、无 capabilities 的专用用户；systemd 进一步只开放工作目录写权限。Bubblewrap 与 Docker 一样共享宿主 Linux 内核，不能视为抵御未知内核漏洞的 MicroVM 强边界；适合受控用户或中等信任的代码执行，若要承载公开、恶意、多租户代码，应把 Runner 节点放进独立 VM 或改用 MicroVM 后端。沙箱返回的 JSON 同样是不可信输出，Runner 和 Engine 都校验协议大小、文件数量、文件名和字节限额；不能把脚本自行打印的“校验通过”当成隔离证明。

超时、取消与故障：沙箱包装脚本限制用户子进程墙钟时间，Runner 另有外部总截止时间。一次性执行的请求取消或外部截止时间到达时，Runner 终止 bwrap 进程树；会话沙箱只终止当次调用的子进程组（客户端断开即提前终止），沙箱本身存活，仅在外部截止（supervisor 失去响应）时销毁重建并在下一次调用报告 `sandboxReset`。`--die-with-parent` 确保 Runner 异常退出时全部沙箱同时退出；mount namespace 和 tmpfs 由内核自动清理，后台回收器按空闲期回收会话沙箱，并清扫 Runner 崩溃遗留且超过一小时的请求输入目录。Engine 每请求默认最多执行 8 次代码，MAF 的 MaxTurns 继续约束模型循环。普通脚本错误通过 stderr 返回供模型修正；网络错误、Runner 不可用、超时和取消不会降级为宿主执行。Runner 请求目前与聊天请求共同存活；不提供断线后继续运行、进程重启后的 Agent 恢复或跨节点调度。

文档能力：固定 Python venv 安装 python-pptx、openpyxl、XlsxWriter、pandas、matplotlib、Pillow 和 defusedxml；主机只读运行时提供 LibreOffice、中文字体和 Node.js。支持生成可编辑 Office 文件，也可在沙箱内调用 LibreOffice 渲染 PDF 后交付。JavaScript 侧只提供 Node 内置模块（ESM 入口 `main.mjs`），不预装 npm 包。

验证：Core 测试验证双重开关、授权撤销、跨租户/用户文件拒绝、二进制资产归属，以及确定性 MAF 循环在错误后再次调用执行工具。Runner 的 Linux 真实测试验证 Bubblewrap 参数与实际边界、网络禁用、宿主文件不可见、任务隔离、内存限制、超时/取消清理、符号链接拒绝、输出截断、tmpfs 容量，以及 PPT/Excel 的生成、重新打开、再次编辑和 PPT 转 PDF，并覆盖 JavaScript 的 Node 执行与输入/输出文件往返。CI 随后发布 Runner 进行真实 HTTP 冒烟，并执行部署脚本、启动强化的 systemd 服务再验收；MAF 联测连接该已安装服务，覆盖 Python 错误反馈、授权 CSV 输入、Excel 生成与再次编辑、文件登记和 `publish_files`（模型与文件存储使用确定性替身）。

## MCP 客户端

使用官方 `ModelContextProtocol.Core` C# SDK 连接外部 MCP Server，把 SDK 返回的 `McpClientTool` 直接交给 MAF Agent。

- 传输：官方 `HttpClientTransport`，支持 Streamable HTTP 和 legacy SSE（默认 Streamable HTTP，显式 `SSE` 兼容 legacy 服务）。
- 协议协商：官方 `McpClient.CreateAsync` 完成初始化与版本协商；自动协商或选择 SDK 支持的五个最低日期版本，并返回协商结果。
- 工具发现：`ListToolsAsync` 返回官方 `McpClientTool`，自动生成 `mcp__{server}__{tool}` 运行时名称；支持读取文本和 Blob 资源。
- 配置目录：MCP Server 独立维护并注册到 Redis，使用 Server 名称作为绑定 ID。
- Agent 绑定：Agent 只保存 `EnabledServerIds`；运行时按 ID 从 MCP 注册表解析配置。
- 故障隔离：单服务器连接失败不阻止其他服务器加载。

```text
MCP 配置页 -> Redis mcp:registry:{serverId}
        |
AgentConfig.Mcp.EnabledServerIds
        v
McpToolFactory（请求级客户端生命周期）
        | official McpClient + ListToolsAsync
        v
McpClientTool.WithName(...) -> ChatClientAgent.ChatOptions.Tools
```

平台只保留 MCP 配置目录、Agent 绑定关系、权限、远程传输选择和请求级资源生命周期，不复制 MCP 协议或工具执行逻辑。旧版 `Mcp.Servers` 仍作为迁移兼容字段读取，新配置不再把 endpoint 复制到 Agent。

限制：仅接入远程 HTTP / SSE MCP（本地 Stdio 不在范围内）；不建立跨请求连接池，一次请求内每个 Server 复用一个客户端；`Http.Url` 必须为完整 MCP endpoint，客户端不自动追加 `/mcp`；MCP 注册表发布不会自动启用服务，必须加入 Agent 配置并发布。

源码：`Backend/src/OpenAgent.Core/Capabilities/Mcp/McpToolFactory.cs`、`McpTransportFactory.cs`。

## RAG

RAG 作为模型可调用的工具参与执行，是否被调用由模型和工具链路共同决定。

| Capability | Description |
|-----------|-------------|
| 文档索引 | 将内容索引到外部 RAG 系统 |
| 语义检索 | 从外部 RAG 系统检索相关文档 |
| 多实例支持 | 同时配置和使用多个 RAG 实例 |
| 适配器扩展 | 通过 `IRagAdapter` 支持不同 RAG 产品 |
| ACL 权限过滤 | 基于用户上下文过滤可见实例 |

| Adapter | AdapterName | Index | Search |
|---------|-------------|-------|--------|
| QdrantAdapter | `qdrant` | 是（需嵌入模型） | 是 |
| RagFlowAdapter | `ragflow` | 否 | 是 |

```text
Agent -> ToolCall("search_knowledge_base")
  -> RagCapabilitySource
  -> IRagService.SearchAsync/SearchDetailedAsync
  -> IRagAdapter -> RAG Backend
```

限制：Qdrant 索引请求中向量字段使用空数组占位，实际使用需调用嵌入模型；RagFlowAdapter 不支持索引；适配器响应解析使用同步 `.GetAwaiter().GetResult()`；无检索结果缓存。

源码：Core `Backend/src/OpenAgent.Core/Capabilities/Rag/RagCapabilitySource.cs`、`RagService.cs`、`Adapters/`；Contracts `Backend/src/OpenAgent.Contracts/Models/IRagAdapter.cs`；测试 `Backend/tests/OpenAgent.Core.Tests/Capabilities/RagCapabilitySourceTests.cs`。后端配置见 [rag](../integrations/rag.md)。
