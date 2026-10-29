# OpenCode ACP 聊天运行时

本分支将聊天默认执行入口切换为 OpenCode，依赖独立的 [ACP SDK](acp-dotnet-sdk/README.md)。用户明确选择 OpenCode；镜像版本由 `Backend/opencode.version` 固定，本机其他版本必须独立验收。实现状态与 PR 顺序只维护在 [任务清单](../planning/TODO.md)，验证事实见实现 PR（#234、#243）。

## 架构与目录

```text
Backend/src/
  OpenAgent.Contracts/Runtime/IAgentRuntimeDriver.cs  平台输入、事件和工具回调契约
  OpenAgent.Application/Execution/
    RuntimeAgentExecutor.cs        端点稳定入口；身份/配置/文件/持久化协调
    RuntimePreparation.cs          能力、MCP、Skill、历史准备
    RuntimePreparationScope.cs     部分结果和资源生命周期
  OpenAgent.Runtime/               平台适配类库，IsPackable=false；根目录为 harness 无关组件
    RuntimeServiceExtensions.cs    AddOpenCodeRuntime 组合入口
    AcpEventMapper.cs              协议更新到既有平台事件；工具名前缀由构造参数指定
    RuntimeToolBridge.cs           MCP 桥：主站端口挂载（MapRuntimeToolBridge）+ 每轮 bearer 注册；无宿主挂载或主站绑定不可达时回退自建 loopback 端点
    AcpSessionMcp.cs               外部 MCP server 定义到 ACP session/new 条目
    OpenCode/                      OpenCode 专属 adapter；未来其他 ACP harness 平行建目录
      OpenCodeRuntime.cs           并发配额、ACP 会话、取消和进程回收
      OpenCodeProcess.cs           可执行文件解析、模型格式、受控环境及 OpenCode 配置
      OpenCodeProcessKeeper.cs     空闲进程保活池：复用、空闲清扫与会话数上限
      OpenCodeSkills.cs            Skill 无链接复制到 .opencode/skills
      OpenCodeWorkspace.cs         专属目录权限及链接检查
      OpenCodeOptions.cs           可信宿主配置
  OpenAgent.Runtime.Acp/           单 NuGet SDK，完全独立于平台
Backend/tests/
  OpenAgent.Runtime.Tests/         驱动、桥、环境与真实 OpenCode 测试
  OpenAgent.Application.Tests/Execution/AcpExecutionTests.cs
  OpenAgent.Engine.Tests/Hosting/OpenCodeSseTests.cs
```

Runtime 只引用 Contracts、Runtime.Acp 和官方 MCP SDK；Application 只引用平台契约，不接触 ACP 类型。Engine.Host 负责组合。没有增加第二个 SDK NuGet 包，也没有把平台类型移进 SDK。Runtime 根目录只保留对任何 ACP harness 通用的组件（事件映射、工具桥、MCP 透传映射、组合入口）；OpenCode 专属的可执行文件解析、启动配置、Skill 物化与驱动实现收敛在 `OpenCode/` 子目录（命名空间 `OpenAgent.Runtime.OpenCode`）。后续接入其他 ACP harness 时平行新增目录，不改动根目录中性组件。

## 一轮执行

1. HTTP 身份与入口治理不变。平台解析并授权 Agent/模型，建立 Tenant/User/Conversation/Trace 身份，再进入 Host 有界准入；获准后校验附件、获取既有会话锁与版本（见 N03）。
2. 平台把历史、平台自有工具、外部 MCP server 定义和 Skill 包准备成 RuntimeInvocation。历史加载、工具发现与各 feature 工厂互不依赖，并行执行后按注册顺序合并（EF 仓储均按调用创建独立 DbContext，天然并发安全）；工厂失败时已启动的任务进入统一释放路径。平台工具函数带本轮闭包；每次回调校验 Tool/Function 的 invoke 权限，沿用工具超时、独占/只读并发和结果字符预算。外部 MCP server 不再经平台代理连接（见"MCP 透传"），其凭据与限流由 harness 持有。MCP server 定义与 Runtime profile 的读取在单例仓储内有 30 秒 TTL 进程内缓存（含负结果），写入/删除即时失效，跨进程仍有最多 30 秒陈旧窗口。
3. 驱动按复用键（会话归属 + 启动身份哈希）获取保活进程，未命中或已过期时才启动新的 `opencode acp` 进程并执行 initialize。进程拥有独立的主目录（HOME/XDG 状态目录与进程工作目录），跨轮次保活时保持 harness 缓存为热；每轮仍创建随机工作区作为 ACP 会话 cwd。平台自有工具经宿主共享 MCP 桥暴露：桥是宿主单例，Engine.Host 经 `MapRuntimeToolBridge` 把桥路由挂到主站已有端口（自闸门在用户上下文/准入中间件之前短路桥流量），没有独立监听站点；库/测试等无宿主挂载、或主站无可 loopback 直连的 http 绑定时，回退为自建 loopback 端点。每轮向共享桥注册本轮工具集并持有独立 256 bit 随机 bearer，身份不取自模型参数，整轮取消或租约释放后拒绝新请求。启用的 Skill 包复制到本轮工作区 `.opencode/skills/<name>`，拒绝包根目录及包内符号链接/reparse point；通过显式 `skills.paths` 交由 OpenCode 原生 skill 工具发现。
4. 复用进程直接跳过启动与 initialize；随后执行 session/new（携带本轮平台桥 + 透传的外部 MCP server 定义，cwd 为本轮随机工作区）→ 可用时选择 model config → session/prompt。OpenCode 直接调用选定的模型，平台工具经 MCP 桥调用，外部 MCP 工具由 OpenCode 原生连接调用。
5. 消费 session/update，合并工具增量、保留调用 ID、去除桥的显示名前缀，将正文、reasoning、工具结果和计划映射到 AgentStreamEvent。相同工具参数更新不重复发送；参数确有变化时沿同一个 ID 更新。
6. ACP prompt 结束后排空更新队列。平台保存会话成功后才发送 usage/done。失败或取消保存部分正文、reasoning 与工具记录。
7. 取消发送 session/cancel 并等待原 prompt 结果；SDK 对不确认的进程执行有界回收。轮次结束后关闭本轮 ACP 会话、吊销本轮桥注册（共享端点保留给后续轮次）、释放工具资源并删除本轮随机工作区，保留平台会话数据。完整成功轮次的进程归还保活池等待追问复用；失败、取消、空闲到期、会话数或总量超限的进程立即销毁并回收其主目录。

OpenCode 的原生工具默认 `* = deny`，仅允许 `openagent_*` 平台桥工具；按 Agent 配置透传的外部 MCP server 会追加对应 `server_*` 放行，启用 Skill 时仅按本轮授权名称放行原生 `skill`，其余技能名称保持拒绝。宿主可通过 `OpenCode:AllowedNativeTools` 显式放行原生工具（如 `read`/`edit`/`write`，可带参数模式如 `bash[npm *]`），默认空保持全拒绝；放行即把对应主机读写/执行面直接交给 harness 进程（应用层授权，非 OS 沙箱），由宿主知情选择，白名单参与进程复用身份，改动后不复用旧权限进程。条目禁止花括号（OpenCode 在 JSON 解析前展开 `{file:...}`/`{env:...}` 宏），启动校验拒绝。底层及生产基础配置的 `AllowedNativeTools` 仍为空；开发配置保留原有显式授权 `read/edit/write/bash`，代码执行走 OpenCode 自身。生产需要代码执行时，必须在干净服务身份/受控执行环境中显式配置同样的所需权限，不能依赖 Runner 兜底。Client 不启用文件/终端回调，ACP 权限请求默认拒绝。桥路由挂载在主站端口上时，仅接受 loopback 远端（其余一律 404，不暴露路由存在性）与命中本轮注册的 bearer（未命中 401），公共认证与准入管线不感知桥流量。独立 Runner 沙箱已移除：平台不再提供 execute_code/workspace 文件工具/run_skill_script，代码执行完全依赖宿主放行的 OpenCode 原生工具。文件资产策略继续约束平台侧工具。这是应用层授权和进程环境隔离，**不是操作系统沙箱**；本轮不宣称解决恶意 harness 的主机访问问题，也不实现人工审批。

## MCP 透传

- Agent 配置启用的外部 MCP server（租户注册表 `EnabledServerIds` 或内联 `Servers`）经租户过滤与逐 server 授权后，直接作为 ACP `session/new` 的 mcpServers 条目交给 OpenCode 原生连接。
- 平台侧不再建立外部 MCP 客户端：McpClientPool、McpToolFactory、read_mcp_resource 与延迟加载（search_tools/DeferredToolInjector）已删除；平台桥只承载平台自有工具（文件、能力、`read_skill_file`）。
- 安全取舍必须知情：透传后外部 server 的凭据（当前配置仅 URL/类型，鉴权头为后续项）由 harness 进程直接持有，逐调用授权、超时与预算不再经过平台；harness 与 server 之间的网络路径也不再受平台管控。

## Skill 物化

- 启用的 Skill 包物化到进程级内容缓存目录（键 = 租户 + 对象键 + 包 SHA-256，首次仍逐文件下载并校验完整性，staging 原子提交；未登记哈希的记录退回一次性目录随轮释放），再复制到工作区 `.opencode/skills/<name>`，拒绝包根及包内的符号链接/reparse point，避免把宿主目标引入工作区或通过工作区写回共享缓存；复制逐文件响应取消。追问轮次命中缓存仍零下载，但每轮承担本地复制成本，新包发布自然失效旧键。缓存根目录按进程隔离（含进程 ID）；轮次以租约持有包引用，淘汰只回收零引用条目，活跃包不会被删除。OpenCode 原生 skill 工具按需加载 SKILL.md，平台不再把 skill 文本注入提示词。
- Skill 包内文件的受限读取（`read_skill_file`）作为平台桥工具提供，限制在物化包目录内；包内脚本不再有平台执行路径，Skill 物化进工作区后可由宿主放行的 OpenCode 原生工具（如 `bash`）直接运行。

## 独立 Runtime 设置

Runtime 在独立设置页保存 ID、名称、Provider 和单轮时限，存入 PostgreSQL 的租户隔离表；它不属于 Agent 配置，也不包含模型、Skill 或 MCP 绑定。聊天顶部选择已保存项，通过 `context.runtimeId` 传入；服务器校验当前租户所有权和 Provider。实际时限受宿主上限约束。OpenCode 可选，Claude Code/Codex CLI/Gemini CLI 在前端禁用并由服务器拒绝。原有模型和能力设置及绑定保持原行为。

## 模型与历史

- 聊天顶部独立选择已保存的 LLM 连接并通过 `llmProfileId` 传入；Agent 编辑器不再提供 OpenCode 或内嵌模型配置，也不再显示“使用 Agent 模型”选项。后端暂保留旧 API 的 `AgentConfig.OpenCode.Model` 回退与密钥保护，以兼容已有配置；新前端要求选择独立 LLM 连接。
- 原生 OpenCode 进程完成 ACP `initialize` 后发送 `runtime` SSE 事件，携带驱动 Provider、保存的 Runtime ID/名称及对端报告的 Agent 名称/版本。平台将身份写入 assistant 消息 metadata.extensions，成功、失败/取消均保留已观察到的身份。前端回复下方显示“实际运行时”，刷新从持久化记录恢复；握手失败或旧消息不伪造运行时标识。
- 只将本轮授权后的 Endpoint、ModelId、ApiFormat、Temperature、ContextTokens 交给 OpenCode。ApiKey 用独立子进程环境变量传入，配置中只放 `{env:OPENAGENT_MODEL_API_KEY}`，不拼接到命令、提示词或日志。
- OpenCode 会在 JSON 解析前展开配置宏。驱动在序列化时转义租户文本中的花括号，只还原随机占位的密钥引用，避免 `{file:...}`/`{env:...}` 指令变成宿主文件读取或环境变量展开；有真实进程回归测试。
- 子进程只继承 PATH/PATHEXT/COMSPEC/SystemRoot/WINDIR 白名单，不透传服务数据库口令或云凭据环境变量；用户配置的非注入保证仍依赖干净服务身份（见下文）。PATHEXT 缺失会让按名解析（`python` → `python.exe`）表现为"命令不存在"，Windows 上另会从子 PATH 剔除商店别名目录（`%LOCALAPPDATA%\Microsoft\WindowsApps`——AppModel 激活的存根在受控环境下挂死，且掩盖 PATH 中靠后的真实安装）。启动命令由宿主配置，聊天请求不能替换。宿主显式设置时额外透传 Node TLS 覆盖 `NODE_TLS_REJECT_UNAUTHORIZED`（跳过证书校验）与 `NODE_EXTRA_CA_CERTS`（追加自签名 CA），用于自签名证书的模型端点；未设置不进入子进程环境。
- ApiFormat 映射为 OpenAI Chat Completions、OpenAI Responses、Anthropic 对应的 AI SDK Provider；Chat Completions 由确定性本地模型 + 真实 OpenCode 覆盖；Anthropic Messages 已通过智谱兼容端点真实模型调用，含 Skill/MCP 与 HTTP/SSE。OpenAI Responses 仍待真实服务商互操作验收；不据此宣称官方 Anthropic Claude 模型已验证。
- ContextTokens 未配置时用 128000 作为驱动配置回退值；输出上限为上下文的四分之一且不超过 16384。实际模型限制应在平台模型 profile 中配置，不把回退值当作服务商能力。
- 每轮使用新的 ACP session 与随机工作区；OpenCode 进程按"会话归属 + 启动身份"（可执行文件、模型端点/密钥/参数、指令、MCP/Skill 放行策略的哈希）在 `KeepAliveSeconds` 窗口内保活复用，追问轮次免付进程冷启动，且不跨会话归属共享进程。旧消息按 role/content 序列化为一个嵌入 JSON 资源；当前 query 和平台筛选后的图片作为内容块传入。平台会话主键不变，但不沿用 OpenCode 私有会话或 ACP session/load。历史中的工具结果是上下文数据，不会自动重放执行。
- usage_update.used 是上下文占用量，不是本轮计费 token。harness 上报 usage_update 时终态 Usage 事件携带上下文占用（prompt=total=used、completion=0），不拆分计费；未上报时 TokenUsage 保持 null，ModelId/TraceId 仍随终态返回，会话持久化记录不落 TokenUsage。

## 配置与启动

免 Docker 的本机启动、前端配置与数据生命周期见 [本机预览](../operations/local-preview.md)。

开发机安装 OpenCode（不固定版本），先验证 `opencode --version`。首个执行轮次按以下顺序解析可执行文件（`OpenCode:Executable`，默认 `opencode`）：配置为路径且存在时直接使用；否则依次探测 PATH 目录直接命中、各 PATH 目录与 npm 默认全局根（`%APPDATA%\npm`）下的 npm 安装布局（`node_modules/opencode-ai/bin`、`node_modules/opencode-windows-x64[-baseline]/bin`）；Windows 上仍找不到时经 `cmd.exe` 转发 `.cmd/.bat` shim 兜底，全部未命中时报 `DependencyUnavailable` 并附带安装指引。Linux/macOS 的 npm 启动器可直接执行，PATH 命中即可。Engine 镜像内嵌的 Linux 原生二进制版本由仓库根的 `Backend/opencode.version` 锁定，`scripts/build-images.*` 与 Release 工作流读取该文件并经 `OPENCODE_VERSION` 构建参数传入；OpenCode 本体自包含、运行无需 Node/npm，镜像另含 python3 与 node 22（npm 不随镜像分发），供宿主放行的原生工具（如 `bash`）与脚本在容器内执行——这扩大的是容器内可执行面，不改变应用层授权的性质。镜像 label 记录 `io.openagent.opencode-version`，Engine 发布标签带 `-oc<版本>` 后缀。`latest` 仅保留给手动 `docker build` 的临时构建，环境变量 `OPENAGENT_OPENCODE_VERSION` 用于升级前的临时验证，提交仍以版本文件为准。升级 OpenCode 时先修改版本文件，并以 `docker build --target runtime-tests` 通过真实进程回归后再发布。

```json
{
  "OpenCode": {
    "Executable": "opencode",
    "Arguments": ["acp"],
    "RequestTimeoutSeconds": 30,
    "TurnTimeoutSeconds": 1800,
    "MaxConcurrentTurns": 8,
    "MaxPromptCharacters": 2097152,
    "LogUsageUpdates": false,
    "KeepAliveSeconds": 0,
    "MaxKeptProcesses": 8,
    "MaxSessionsPerProcess": 64,
    "AllowedNativeTools": []
  }
}
```

`WorkspaceRoot` 默认是系统临时目录下的 `openagent-acp`；镜像改用 `/var/lib/openagent/opencode`。必须为绝对路径，父目录由运维可信地提供，根目录由服务账户独占；Unix 创建模式为 0700，已有目录若开放组/其他用户访问则拒绝使用，需运维先收紧权限。Windows 需预置仅服务账户可访问的 ACL。根目录和各 owner/随机轮次/进程目录拒绝链接。启动时验证配置格式，首轮创建目录时验证文件系统。并发上限为每个 Host 的进程租约配额，不是已验证的生产容量。Host 有界准入先于准备资源；驱动物理执行门额外等待最多 RequestTimeoutSeconds 并响应调用者取消。驱动整轮时限从拿到物理执行空位后开始，包含初始化和推理；Host 执行时限更早，从准入成功起包含准备和持久化。RPC 时限限制初始化/配置等调用，prompt 由整轮时限控制。工具桥复用主站端口；主站仅绑定 https 或特定非通配地址、无法 loopback 直连时，自动回退为独立 loopback 监听。

`LogUsageUpdates` 打开后，每轮 `usage_update`（上下文占用 used/size）以 Information 级别写入日志（EventId 4500），用于追踪各轮 token 占用；不落对话报文，生产环境保持关闭。

每轮另有一行分段计时日志（EventId 4501，Information，默认开启，不含对话内容）：`process`（进程获取/启动+initialize，近 0 表示命中保活）、`sessionNew`（ACP session/new + 桥租约）、`prompt`（模型配置+发送）、`firstUpdate`（prompt→首个 session/update，衡量 ACP 链路活性）、`firstToken`（prompt→首个可见 token，衡量模型 TTFT）、`stream`（首 token→轮次完成）、`close`（会话关闭+清理+归还）。用于判别延迟归属：ACP 侧异常会体现在 sessionNew/firstUpdate，模型侧慢会体现在 firstToken/stream。

进程保活（默认关闭）：`KeepAliveSeconds > 0` 时，相同"会话归属 + 启动身份"的空闲进程在窗口内可被下一轮复用（实测可把追问轮次从约 2.5 s 降到约 1 s）；`MaxKeptProcesses`（默认 8）限制保活进程总数，`MaxSessionsPerProcess`（默认 64）限制单进程累计会话数，超限即销毁重建，后台清扫周期不超过 5 秒。失败或取消的轮次不归还进程；身份哈希覆盖模型端点/密钥与指令、工具放行策略，且不跨会话归属复用。**默认 0 的原因**：实测 OpenCode 1.19.3 空闲进程存在约 1–2 核的持续 CPU 空转与持续外呼（会话服务/watcher，与平台侧桥的存亡无关），多进程保活会拖慢整机；确认目标版本空闲行为可接受后再按部署开启。工具桥与保活开关无关：桥是宿主共享单例（挂载主站端口，回退自建 loopback），每轮以独立 bearer 注册/吊销工具集，不随进程销毁。

缺少可执行文件、启动失败或 ACP 异常会明确失败，没有第二条执行路径。外部错误正文不会原样进入 SSE/会话；日志记录异常类型。工作区清理失败会告警并保留可定位目录，不删除其他轮次或宿主根目录。

## 兼容组件与后续范围

MAF（Microsoft.Agents.AI 全系包）已在本 PR 移除：PlatformChatHistory 改为显式 Load/Persist 生命周期的普通适配器；跨轮自动压缩已移除：每轮全新 ACP session 下上下文整理由 OpenCode 原生负责（单轮内自压缩并上报 compaction_update），平台仅保留手动压缩端点（分组/摘要策略/审计复用原实现）；上下文提供器为平台抽象。Microsoft.Extensions.AI 仍作为工具（AIFunction）与摘要客户端的底层抽象保留；OpenAI Responses/Anthropic 摘要客户端由 ModelBindingChatClient 绑定模型（原 MAF 的 stored-output 禁用扩展随包移除）。

OpenCode 内部可能产生标题等辅助模型请求，本轮未采集其底层 HTTP 请求/响应到平台 LLM interaction 表，也不据此伪造 usage。

Worker/分布式租约与 fencing、RunGrant、跨重启事件重放、人工审批、MCP server 鉴权头透传和其余服务商验收仍按任务清单单独推进。本轮没有部署或修改线上配置。需要回退时回退此平台接入 PR；独立 SDK PR 可继续保留。

## 验证方法

默认执行 `dotnet build Backend/OpenAgent.sln` 和 `dotnet test Backend/OpenAgent.sln --no-build`。真实 OpenCode 场景显式设置可执行文件路径：

```sh
export OPENAGENT_TEST_OPENCODE=/absolute/path/to/opencode
dotnet test Backend/tests/OpenAgent.Runtime.Tests
dotnet test Backend/tests/OpenAgent.Engine.Tests --filter FullyQualifiedName~OpenCodeSseTests
```

测试启动临时 Kestrel 模型服务，使用假 key 与确定性响应，覆盖中文/历史、平台工具回喂、取消、错误脱敏，以及 HTTP/SSE → 会话保存 → 续聊。未设置变量时显式跳过真实进程场景，普通边界测试继续运行；不依赖 PostgreSQL/Redis/外部模型账户。

Linux 可通过 Dockerfile 的独立测试阶段复现相同链路：

```sh
docker build -f Backend/src/OpenAgent.Engine.Host/Dockerfile --target runtime-tests \
  --build-arg OPENCODE_VERSION="$(cat Backend/opencode.version)" .
```

SDK 控制台继续只有 SDK 引用，外部连接入口见 [控制台使用](acp-dotnet-sdk/USAGE.md#opencode)。测试事实、镜像验证与未验收范围以实现 PR #234 的描述为准。

官方参考：[ACP 接入](https://docs.opencode.ai/docs/acp/)、[配置](https://docs.opencode.ai/docs/config/)、[Provider](https://docs.opencode.ai/docs/providers/)、[权限](https://docs.opencode.ai/docs/permissions/)。配置宏的解析顺序曾按 [1.18.35 配置源码](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/config/config.ts)核对（历史验证事实）。官网与二进制随版本演进，验收以实际安装的 OpenCode 二进制为准。


## N02：宿主继承缓解与受支持模式

初始基线为 `origin/dev@768a8c1e`，交付前已合入 `origin/dev@45802efd`（保留 SkiaSharp 依赖、Runner 移除与附件工作区文件化）。范围为 Runtime/OpenCode、Engine 镜像/开发配置、Data Protection 卷挂载和 Runtime.Tests。N02 不修改 SDK 协议、依赖白名单、RAG 或 Worker/RunGrant 实现；Runner 移除来自已合入的最新 dev。代码执行按用户要求走 OpenCode 自身；最新 dev 已移除 Runner，本 PR 不恢复该路径。`KeepAliveSeconds` 保持 0。

### 当前缓解

- HOME 与 USERPROFILE 指向同一个进程私有 home；APPDATA/LOCALAPPDATA、四个 XDG 目录、OPENCODE_CONFIG_DIR、TMPDIR/TMP/TEMP 均收拢到 `p-<random>/.state`，进程 cwd 与 ACP 轮次 cwd 分离。API key 仍仅通过该子进程白名单环境传入。PATH 与 Node TLS 覆盖仍属于可信宿主配置，并非出站访问控制。
- 固定设置 `OPENCODE_DISABLE_PROJECT_CONFIG`、`OPENCODE_DISABLE_EXTERNAL_SKILLS`、`OPENCODE_DISABLE_CLAUDE_CODE`，阻止祖先项目配置/指令与 `.claude/.agents` 自动扫描；授权 Skill 由相对 ACP cwd 的显式路径发现。关闭默认插件、模型目录抓取和 LSP 下载，避免这些默认扩展来源；这不代表整个进程已无外呼或可启用保活。
- 保留配置宏转义、底层原生工具默认拒绝及 ACP 文件/终端回调关闭；开发配置继续显式放行 `read/edit/write/bash`，保持 OpenCode 自身代码执行能力。Runner 不作为本轮执行方案。按名称授权 Skill 避免附带放行 OpenCode 内建或其他磁盘技能。目录检查与包复制防止正常执行中误跟随链接，不承诺抵抗同 UID 恶意进程并发替换路径的竞态。
- Engine 发布镜像及 `runtime-tests` 阶段使用 .NET 镜像的非 root `app` 身份（UID 1654），干净系统 home 为 `/home/app`。Engine 和 OpenCode 仍共享这一身份；它消除开发者账户的隐式继承，不能隔离 Engine 自身的配置、进程内凭据或其他租户。

上游 1.19.3 的 [Skill 扫描](https://github.com/anomalyco/opencode/blob/v1.19.3/packages/opencode/src/skill/index.ts)、[扫描开关](https://github.com/anomalyco/opencode/blob/v1.19.3/packages/opencode/src/effect/runtime-flags.ts)、[全局 home](https://github.com/anomalyco/opencode/blob/v1.19.3/packages/core/src/global.ts)、[配置目录](https://github.com/anomalyco/opencode/blob/v1.19.3/packages/opencode/src/config/paths.ts)与[配置合并](https://github.com/anomalyco/opencode/blob/v1.19.3/packages/opencode/src/config/config.ts)说明了为什么不能只改 HOME：系统身份的 `~/.opencode` 仍可进入配置来源，系统 managed config 也能参与合并。测试专用 `OPENCODE_TEST_HOME` / `OPENCODE_TEST_MANAGED_CONFIG_DIR` 只用于构造临时反例，生产代码不设置或透传它们。macOS managed preferences 也不在上述开关的隔离保证内。

| 模式 | 适用边界 | N03/N04 使用条件 |
|---|---|---|
| OpenCode 原生执行：干净独立 OS 服务账户，或一次性非 root 测试容器 | 仅可信开发/单一信任域；系统 home、祖先目录、XDG 与 managed config 均由运维确认干净，不挂载个人 HOME/凭据/socket；显式授权所需 read/edit/write/bash。开发配置保留此授权 | 固定 1.19.3、专属目录，记录实际工具列表和 Node/Python 版本；N03 冷启动基线保活 0，确定性本地模型，并包含真实代码执行负载；N04 仅实验性调整保活后恢复 0 |
| 最小工具模式（底层默认拒绝） | 用于纯聊天/平台工具的独立对照，不能替代原生代码执行验收；external_directory 仍为单独权限，不因放行 bash 自动开放 | 与执行模式分别报告，不能用该模式数据声称 Node/Python 执行容量；跨目录探针另行标记其权限 |
| 个人账户、脏服务账户、共享 Engine 容器承载互不信任租户 | 不获多租户安全验收；环境白名单与应用权限不足以形成 OS 隔离 | 不用于得出多租户安全或生产容量结论 |

Windows 原生运行必须使用独立服务账户、干净系统用户 profile 和受限目录 ACL，并为该账户安装/指定绝对路径的 OpenCode。不要依赖覆盖 USERPROFILE 修复系统 homedir。本轮没有 Windows 测试机，原生 Windows 1.19.3 的 homedir、junction/ACL、进程回收均待该环境实测；Unix 的 0700 测试不能替代它。

### 面向不可信多租户的后续执行边界（与 N16.2 Worker 联动）

每次运行分配独立受限执行环境，默认轮次结束销毁；Worker 控制面只负责投递已授权输入、事件和取消，不把 Engine 的环境或文件树带入执行环境。最低验收如下，尚未在 N02 实现：

| 边界 | 部署要求与验收 |
|---|---|
| 文件 | 只读最小根文件系统；每轮独享 scratch/home/tmp 与授权输入副本；不挂载 Engine 配置、密钥、Docker socket、宿主 HOME 或共享可写 Skill 缓存。逃逸路径、绝对路径、符号链接/junction 和跨租户同名资源攻击必须在 OS 层被拒绝 |
| 进程/身份 | Linux 独立 user/mount/PID/IPC namespaces、非特权 UID、drop capabilities、no-new-privileges、seccomp/LSM；不共享 host PID。Windows 用每轮受限 VM/Hyper-V 等可验收边界，Job Object 单独不足以提供文件/网络隔离。跨轮/租户不可观察或终止对方进程 |
| 资源 | cgroup v2 或等效约束 CPU、memory/swap、pids、文件描述符、临时盘/产物配额及墙钟时限；租约失联、取消或超时终止整个执行组并清理，OOM/超额事件可归因于该轮 |
| 出站 | 默认拒绝；仅经受控代理放行授权模型/MCP 目标；阻断云 metadata、宿主和内部网络、任意 DNS/重定向绕过。仅把本轮所需短期凭据交给运行环境；平台桥应由 Worker 提供可达且鉴权的转接，不能直接沿用同宿主 loopback 假定 |
| 生命周期 | 运行标识与 Tenant/User/Conversation 绑定，禁止跨身份复用；仅在完整隔离边界内评估保活。平台取消、Worker 失联、协议中断、进程逃逸与清理失败均需有界处置与审计；RunGrant/分布式 fencing 由对应任务实现 |

### 2026-10-28—29 验证与未验收项

- 最新基线 `45802efd` 已移除 Runner，测试数量随之变化。macOS arm64、SDK 10.0.301：全解决方案构建成功，默认后端 972 通过、25 跳过、0 失败；跳过为真实 OpenCode/Windows 环境门控。现存 nullable 与 SkiaSharp 过时 API 告警未在 N02 修改或压制。
- Linux arm64、SDK 10.0.403：标准 Dockerfile 的 `runtime-tests` 与 `runtime` 两个目标均构建成功，OpenCode 固定为 1.19.3。测试使用非 root UID 1654 的干净 app profile、临时 canary、确定性本地模型和假 key；未读取真实用户凭据。此前缓存镜像复测与独立 npm tarball SHA-512 校验仍可辅助复现，最终门禁以标准 Dockerfile 结果为准。
- 真实 Runtime 套件 88 通过、1 个 Windows 专属用例跳过、0 失败（89 总数包含普通边界测试）；Host SSE 1 通过。真实进程夹具使用同一非并行测试集合，避免多个独立冷启动夹具争抢资源；首次未分组运行曾出现技能/MCP 45 秒超时，独立复测及最终分组全套通过。生产超时和并发配置未调整，容量由 N03 的显式负载验证。
- `OpenCodeNativeExecutionTests` 读取实际开发配置，由真实 OpenCode 通过 bash 执行 Node/Python；覆盖工作区附件读取、本地模块加载、JSON 文件读写、私有临时目录、非 root 身份和结果回传。执行路径不依赖 Runner。第三方依赖仍需在执行环境预置，本次不引入旧 Runner 的依赖集合。
- 发布镜像本地检查：UID 1654、Node 22.23.4、Python 3.12.3、OpenCode 1.19.3；状态目录及 Data Protection 目录可由服务账户写入。镜像只在本机构建，未发布或部署。
- `OpenCodeIsolationTests` 包含：探针子进程环境/cwd/temp、可注入对照组与收紧后无注入、脏身份 .opencode 配置仍注入的反例、默认强制越界 read 拒绝、跨租户/用户同名会话与 Skill、显式授权 shell/外部目录后的文件与同级进程可见性。`OpenCodeSkillsTests` 覆盖路径穿越、包根/包内链接拒绝、副本不回写缓存及取消；既有真实取消/失败用例检查轮次工作区及进程 home 回收。
- 最新部署脚本测试 10 通过、布局测试 3 通过。合成 Data Protection key-ring 已验证 root 写入、迁移位置/权限后 UID 1654 解密、迁回旧位置后 root 解密；未使用或迁移实际部署密钥。
- Windows 原生账户/ACL/junction/进程回收、真实服务商、实际部署与未来 Worker 多租户隔离未验收；探针子进程不等于真实 harness，Linux /proc 结果不能替代 Windows 进程视图验收。保活空闲阈值仍未测定，默认保持 0。

### 回退

保持 `KeepAliveSeconds=0`。出现兼容问题时停用受影响 Runtime/能力或回退该 PR；不能把恢复个人 HOME、允许宿主技能继承作为安全回退；原生工具授权也不能代替多租户 OS 隔离。Skill 每轮复制增加磁盘开销，N03 需计入准备/清理成本。非 root 镜像的 Data Protection 卷权限迁移与旧镜像回退见[部署指南](../operations/deployment.md#opencode-服务身份与已有密钥卷)。既有 `WorkspaceRoot` 若权限过宽，应停机后由运维收紧为专属目录，不让应用自动更改未知宿主目录权限。


## N03：并发准入、排队与取消

基线为 `origin/dev@d38a3513`（N02 已合入）；实现入口为 `Backend/src/OpenAgent.Application/Execution/Admission/`。Host 通过 Application 的单例 TurnAdmission 统一覆盖 sync、intent 和 stream，Agent/模型授权与所选 Runtime 校验在准入之前。会话元数据可用于授权/选 Agent：省略 AgentId 时仅通过 GetMetadataAsync 读取 UserId/AgentId/软删除标记，EF 直接投影会话表，写穿层绕过完整记录缓存及预热；消息历史、会话锁、附件及工具资源仅在获准后准备。Application 不引用 Runtime/ACP，纯状态和错误信息留在 Contracts。

配置节 `RuntimeAdmission`：

| 字段 | 默认 | 约束/含义 |
|---|---:|---|
| MaxConcurrentTurns | 2 | Host 活跃轮次上限，1–128；包括准备、执行、终态持久化及清理 |
| MaxQueuedTurns | 8 | Host 等待上限，0–4096；0 表示关闭排队并立即拒绝 |
| MaxConcurrentPerTenant | 1 | 单租户活跃上限，1–Host 活跃上限 |
| MaxQueuedPerTenant | 4 | 单租户等待上限，0–Host 等待上限 |
| QueueTimeoutSeconds | 30 | 排队截止时间，1–3600 秒；不消耗执行时限 |
| ExecutionTimeoutSeconds | 1800 | 从准入成功起到准备/执行/持久化结束的取消时限，1–86400 秒；与所选 Runtime 的时限取较小值 |
| RetryAfterSeconds | 1 | 队列满/排队超时提示的重试间隔，1–3600 秒；不自动重试有副作用的轮次 |

字段需成组调整，不能将租户限额设得高于 Host 上限；格式/范围错误在启动时失败。配置为 Host 局部、启动时固定，不提供分布式容量或热改现存队列。`OpenCode:MaxConcurrentTurns` 保留最后的活跃进程租约门，默认仍为 8；低于 Host 限额时还可能发生驱动内的有界等待，部署宜将两个活跃上限对齐。本任务不改保活池、KeepAliveSeconds 默认 0 或 MaxKeptProcesses；打开保活后的空闲进程资源与最终配额由 N04 独立验收。

队列按进入顺序挑选符合租户活跃限额的最早请求；已饱和租户的等待项不会阻塞其他租户。同租户保持 FIFO，但这不是严格的租户轮转或跨 Host 调度。排队取消/超时立即移除等待项；取消与授予竞态通过单一锁串行化，并在准备前再次检查请求 token。活动轮次取消后必须先回收进程/工具、写回中止状态并释放会话锁，最后归还准入名额。排队中止不启动 OpenCode、不创建本轮消息或保存虚假的运行终态；已开始的用户取消/失败沿用部分结果持久化，执行截止导致的取消映射为 Failed 和 RuntimeExecutionTimeout。

HTTP/SSE 行为见[执行协议](../modules/execution.md#执行编排)。状态事件的 started 表示获准准备，不能当作模型首 token。Meter `OpenAgent.Admission` 已接入宿主现有 OpenTelemetry：`openagent_admission_active`、`openagent_admission_waiting`、`openagent_admission_rejected`、`openagent_admission_queue_duration` 与 `openagent_admission_execution_duration`，时长单位秒；无租户/会话标签，避免指标基数和身份泄漏。进程数/CPU/RSS 由容量夹具在测试进程及其子进程树采样，TTFT 从 HTTP 请求发出到首个正文事件，另保留原有 OpenCode 分段日志。

### 验证与容量基线（2026-10-29）

本分支默认后端 1002 通过、27 项环境门控跳过、0 失败；前端 168 通过且构建通过；架构门禁未放宽。全解决方案构建成功，干净编译保留已有 SkiaSharp 过时 API 与 RuntimeBoundaryTests nullable 告警，增量构建可为 0 警告。非 root Linux 容器真实 Runtime 套件 89 通过、1 项 Windows 用例跳过，真实 Host SSE 1 通过。

默认验证覆盖：租户限额/FIFO/跳过饱和租户、Host 与租户队列满、零队列、排队超时、排队取消、取消/释放竞态、授权先于准入、准备/进程不提前启动、执行取消与超时、feature/会话锁回收、HTTP 错误与流内 error/done、前端等待展示和状态清除。真实 OpenCode 与环境范围另列，默认门控跳过不计通过。

复现入口为 `OpenCodeCapacityTests`，显式设置 `OPENAGENT_TEST_OPENCODE`、`OPENAGENT_RUN_CAPACITY=1` 与可写的 `OPENAGENT_CAPACITY_OUTPUT` JSON 路径；只支持 macOS/Linux 的 ps 采样。容器可用显式目标复现（常规发布目标不运行压测，也不增加 procps）：

```sh
docker build -f Backend/src/OpenAgent.Engine.Host/Dockerfile --target capacity-tests --build-arg OPENCODE_VERSION=1.19.3 -t openagent-capacity:local .
docker run --init --name openagent-capacity-check --network none openagent-capacity:local
docker cp openagent-capacity-check:/tmp/openagent-capacity.json /tmp/openagent-capacity.json
docker rm openagent-capacity-check
```

必须在 N02 干净服务身份/一次性非 root 容器下运行，不能把个人账户试跑当生产容量。固定 OpenCode 1.19.3、保活 0，Host/租户/驱动活跃上限同步按 1/2/4/8 提高；每阶同时提交两倍于活跃上限的独立会话，故同时覆盖排队与执行。确定性本地模型每次响应等待 100ms，先调用原生 bash 执行 Node/Python JSON 文件读写再返回正文；显式工具 read/edit/write/bash，真实模型和外部 MCP 不参与。采样每约 250ms 一次，进程树 CPU 超过 600%（单核=100%）连续三次或 RSS 超过 3GiB 时取消本阶并停止后续阶梯；阈值是本机保护线，不是生产 SLO。小样本 P95 仅供本机比较，不作容量承诺。

本机 Apple M5（10 个逻辑核、16GiB）、macOS 26.5 / Docker Linux arm64（Linux 6.12.76-linuxkit，10 vCPU、约 7.75GiB VM 内存），非 root UID 1654，网络关闭且无个人目录挂载；SDK 10.0.403、Node v22.23.4、Python 3.12.3、OpenCode 1.19.3。最终容量实测使用仓库显式 capacity-tests 目标，单独运行以避开其他测试负载。样本为冷进程，每轮均经过原生代码执行。CPU/RSS 是 testhost 及后代总和，ps 的 CPU 百分比为进程历史平均采样，不是 cgroup 瞬时 CPU；RSS 含共享页重复计数，不是独占物理内存。

| 活跃上限 / 样本数 | 完成 P50/P95 ms | HTTP TTFT P50/P95 ms | 排队 P95 ms | 峰值 CPU / RSS MiB | 峰值活跃/等待/进程 | 结果 |
|---|---|---|---:|---|---|---|
| 1 / 2 | 2757 / 4482 | 2702 / 4456 | 2696 | 225.2% / 825.1 | 1 / 1 / 1 | 0/2 错误 |
| 2 / 4 | 4138 / 7130 | 4057 / 7062 | 4131 | 471.6% / 1533.8 | 2 / 2 / 2 | 0/4 错误 |
| 4 / 8 | 保护性取消，不计成功分位数 | 无正文 | — | 662.7% / 1346.5 | 4 / 4 / 4 | 8/8 取消，非模型失败 |
| 8 | 未执行：4 并发已触发停止阈值 | — | — | — | — | 无容量结论 |

每阶结束后活跃/等待/进程均为 0，轮次与进程目录清空。4 并发取消后约 450ms 收敛，15 秒上限未触及；前两阶在采样前已完成回收。testhost RSS 基线约 96.8MiB，结束各阶约 145.5/154.0/158.9MiB；托管运行时堆与宿主缓存仍驻留，不能声称宿主内存逐字节回到启动值。首次容器试跑用固定 500ms 检查过早，曾观察到清理中的进程；最终夹具等待指标/进程有界收敛并保留耗时，不放宽回收条件。个人账户上的更早最小工具试跑只作诊断，不列入该容量表。

新增准入默认总活跃 2、单租户 1、总等待 8、单租户等待 4，为此环境的保守起点；部署须按自己的 CPU/内存和负载重新验收，可先降为总活跃 1。驱动原有默认 8 没有提高；它是最终保护门，当前实际 Host 默认仅允许 2 个活跃轮次。该次仅 6 个成功请求，不足以确认稳态错误率、长上下文/大附件/Skill/MCP 负载或生产 SLO。外部真实服务商、Windows 原生、互不信任多租户 OS 隔离、长时间 soak、生产发布均未验收；KeepAliveSeconds 继续 0。

### N03 回退

降低 Host/租户/驱动活跃限额，保持数值相容；或将两级排队上限设为 0，超额请求返回 429。旧客户端继续忽略 execution_status，可回退 UI 提示；不能回到无限队列或无限等待。KeepAliveSeconds 保持 0。回退代码不涉及数据库 schema、消息格式迁移或部署操作。
