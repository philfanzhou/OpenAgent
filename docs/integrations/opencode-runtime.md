# OpenCode ACP 聊天运行时

本分支将聊天默认执行入口切换为 OpenCode，依赖独立的 [ACP SDK](acp-dotnet-sdk/README.md)。用户明确选择 OpenCode；本地互操作固定为 **1.18.35**。实现状态与 PR 顺序只维护在 [任务清单](../planning/TODO.md)，验证事实见 [初次接入记录](../test-reports/2026-10-19-opencode-runtime.md) 与 [独立 Runtime / PostgreSQL 验收](../test-reports/2026-10-21-runtime-postgres.md)。

## 架构与目录

```text
Backend/src/
  OpenAgent.Contracts/Runtime/IAgentRuntimeDriver.cs  平台输入、事件和工具回调契约
  OpenAgent.Application/Execution/
    AgentExecutor.cs               既有公开入口
    RuntimeAgentExecutor.cs        身份/配置/文件/持久化协调
    RuntimePreparation.cs          能力、MCP、Skill、历史准备
    RuntimePreparationScope.cs     部分结果和资源生命周期
  OpenAgent.Runtime/               平台适配类库，IsPackable=false
    OpenCodeOptions.cs             可信宿主配置
    OpenCodeLaunch.cs              模型格式、受控环境及 OpenCode 配置
    OpenCodeRuntime.cs             并发配额、ACP 会话、取消和进程回收
    RuntimeToolBridge.cs           本轮 loopback MCP 端点及 bearer
    AcpEventMapper.cs              协议更新到既有平台事件
    RuntimeServiceExtensions.cs    AddOpenCodeRuntime
  OpenAgent.Runtime.Acp/           单 NuGet SDK，完全独立于平台
Backend/tests/
  OpenAgent.Runtime.Tests/         驱动、桥、环境与真实 OpenCode 测试
  OpenAgent.Application.Tests/Execution/AcpExecutionTests.cs
  OpenAgent.Engine.Tests/Hosting/OpenCodeSseTests.cs
```

Runtime 只引用 Contracts、Runtime.Acp 和官方 MCP SDK；Application 只引用平台契约，不接触 ACP 类型。Engine.Host 负责组合。没有增加第二个 SDK NuGet 包，也没有把平台类型移进 SDK。

## 一轮执行

1. HTTP 身份与入口治理不变。平台解析并授权 Agent/模型，建立 Tenant/User/Conversation/Trace 身份，校验附件，获取既有会话锁与版本。
2. 平台把历史、平台自有工具、外部 MCP server 定义和 Skill 包准备成 RuntimeInvocation。平台工具函数带本轮闭包；每次回调校验 Tool/Function 的 invoke 权限，沿用工具超时、独占/只读并发和结果字符预算。外部 MCP server 不再经平台代理连接（见"MCP 透传"），其凭据与限流由 harness 持有。
3. 驱动创建随机工作区、独立 HOME/XDG 状态目录和 loopback MCP 桥。桥使用 256 bit 随机 bearer，身份不取自模型参数，整轮取消后拒绝新请求。启用的 Skill 包以符号链接（失败退化为复制）物化到工作区 `.opencode/skills/<name>`，交由 OpenCode 原生 skill 工具按需发现。
4. 通过 SDK 启动 `opencode acp`，执行 initialize → session/new（携带平台桥 + 透传的外部 MCP server 定义）→ 可用时选择 model config → session/prompt。OpenCode 直接调用选定的模型，平台工具经 MCP 桥调用，外部 MCP 工具由 OpenCode 原生连接调用。
5. 消费 session/update，合并工具增量、保留调用 ID、去除桥的显示名前缀，将正文、reasoning、工具结果和计划映射到 AgentStreamEvent。相同工具参数更新不重复发送；参数确有变化时沿同一个 ID 更新。
6. ACP prompt 结束后排空更新队列。平台保存会话成功后才发送 usage/done。失败或取消保存部分正文、reasoning 与工具记录。
7. 取消发送 session/cancel 并等待原 prompt 结果；SDK 对不确认的进程执行有界回收。结束后关闭 MCP 桥、释放工具资源并删除本轮随机工作区，保留平台会话数据。

OpenCode 的原生工具默认 `* = deny`，仅允许 `openagent_*` 平台桥工具；按 Agent 配置透传的外部 MCP server 会追加对应 `server_*` 放行，启用 Skill 时放行原生 `skill` 工具。Client 不启用文件/终端回调，ACP 权限请求默认拒绝。平台 Runner、文件资产和 Skill 脚本策略继续约束平台侧工具。这是应用层授权和进程环境隔离，**不是操作系统沙箱**；本轮不宣称解决恶意 harness 的主机访问问题，也不实现人工审批。

## MCP 透传

- Agent 配置启用的外部 MCP server（租户注册表 `EnabledServerIds` 或内联 `Servers`）经租户过滤与逐 server 授权后，直接作为 ACP `session/new` 的 mcpServers 条目交给 OpenCode 原生连接。
- 平台侧不再建立外部 MCP 客户端：McpClientPool、McpToolFactory、read_mcp_resource 与延迟加载（search_tools/DeferredToolInjector）已删除；平台桥只承载平台自有工具（文件、能力、`read_skill_file`/`run_skill_script`）。
- 安全取舍必须知情：透传后外部 server 的凭据（当前配置仅 URL/类型，鉴权头为后续项）由 harness 进程直接持有，逐调用授权、超时与预算不再经过平台；harness 与 server 之间的网络路径也不再受平台管控。

## Skill 物化

- 启用的 Skill 包按轮次从对象存储物化到平台临时目录（沿用完整性校验），再以符号链接进入工作区 `.opencode/skills/<name>`（Windows 无符号链接权限或跨卷时退化为复制）；OpenCode 原生 skill 工具按需加载 SKILL.md，平台不再把 skill 文本注入提示词。
- Skill 包内文件的受限读取（`read_skill_file`）与脚本执行（`run_skill_script`，经隔离 Runner，保留三重开关）作为平台桥工具提供；两者都限制在物化包目录内。

## 独立 Runtime 设置

Runtime 在独立设置页保存 ID、名称、Provider 和单轮时限，存入 PostgreSQL 的租户隔离表；它不属于 Agent 配置，也不包含模型、Skill 或 MCP 绑定。聊天顶部选择已保存项，通过 `context.runtimeId` 传入；服务器校验当前租户所有权和 Provider。实际时限受宿主上限约束。OpenCode 可选，Claude Code/Codex CLI/Gemini CLI 在前端禁用并由服务器拒绝。原有模型和能力设置及绑定保持原行为。

## 模型与历史

- 聊天顶部独立选择已保存的 LLM 连接并通过 `llmProfileId` 传入；Agent 编辑器不再提供 OpenCode 或内嵌模型配置，也不再显示“使用 Agent 模型”选项。后端暂保留旧 API 的 `AgentConfig.OpenCode.Model` 回退与密钥保护，以兼容已有配置；新前端要求选择独立 LLM 连接。
- 原生 OpenCode 进程完成 ACP `initialize` 后发送 `runtime` SSE 事件，携带驱动 Provider、保存的 Runtime ID/名称及对端报告的 Agent 名称/版本。平台将身份写入 assistant 消息 metadata.extensions，成功、失败/取消均保留已观察到的身份。前端回复下方显示“实际运行时”，刷新从持久化记录恢复；握手失败或旧消息不伪造运行时标识。
- 只将本轮授权后的 Endpoint、ModelId、ApiFormat、Temperature、ContextTokens 交给 OpenCode。ApiKey 用独立子进程环境变量传入，配置中只放 `{env:OPENAGENT_MODEL_API_KEY}`，不拼接到命令、提示词或日志。
- OpenCode 会在 JSON 解析前展开配置宏。驱动在序列化时转义租户文本中的花括号，只还原随机占位的密钥引用，避免 `{file:...}`/`{env:...}` 指令变成宿主文件读取或环境变量展开；有真实进程回归测试。
- 子进程只继承 PATH/SystemRoot/WINDIR 白名单，不继承服务数据库口令、云凭据或用户 OpenCode 配置。启动命令由宿主配置，聊天请求不能替换。
- ApiFormat 映射为 OpenAI Chat Completions、OpenAI Responses、Anthropic 对应的 AI SDK Provider；Chat Completions 由确定性本地模型 + 真实 OpenCode 覆盖；Anthropic Messages 已通过智谱兼容端点真实模型调用，含 Skill/MCP 与 HTTP/SSE。OpenAI Responses 仍待真实服务商互操作验收；不据此宣称官方 Anthropic Claude 模型已验证。
- ContextTokens 未配置时用 128000 作为驱动配置回退值；输出上限为上下文的四分之一且不超过 16384。实际模型限制应在平台模型 profile 中配置，不把回退值当作服务商能力。
- 每轮使用新的 OpenCode 进程与 ACP session。旧消息按 role/content 序列化为一个嵌入 JSON 资源；当前 query 和平台筛选后的图片作为内容块传入。平台会话主键不变，但不沿用 OpenCode 私有会话或 ACP session/load。历史中的工具结果是上下文数据，不会自动重放执行。
- usage_update.used 是上下文占用量，不是本轮计费 token。没有可靠本轮用量时 TokenUsage 保持 null，ModelId/TraceId 仍随终态返回。

## 配置与启动

免 Docker 的本机启动、前端配置与数据生命周期见 [本机预览](../operations/local-preview.md)。

开发机安装并固定 OpenCode 1.18.35，先验证 `opencode --version`；启动 Engine.Host 前确保该可执行文件在 PATH，或配置绝对路径。Engine 镜像包含同版本的 Linux 原生二进制，无需在运行容器内安装 Node/npm。

```json
{
  "OpenCode": {
    "Executable": "opencode",
    "Arguments": ["acp"],
    "RequestTimeoutSeconds": 30,
    "TurnTimeoutSeconds": 1800,
    "MaxConcurrentTurns": 8,
    "MaxPromptCharacters": 2097152
  }
}
```

`WorkspaceRoot` 默认是系统临时目录下的 `openagent-acp`；若覆盖，必须为绝对路径且由服务账户独占。启动时验证配置。并发上限为每个 Host 的进程租约配额，不是已验证的生产容量。超额请求等待空位并响应调用者取消；整轮时限从拿到空位后开始，包含初始化和推理。RPC 时限限制初始化/配置等调用，prompt 由整轮时限控制。

缺少可执行文件、启动失败或 ACP 异常会明确失败，没有第二条执行路径。外部错误正文不会原样进入 SSE/会话；日志记录异常类型。工作区清理失败会告警并保留可定位目录，不删除其他轮次或宿主根目录。

## 兼容组件与后续范围

MAF（Microsoft.Agents.AI 全系包）已在本 PR 移除：PlatformChatHistory 改为显式 Load/Persist 生命周期的普通适配器；压缩（分组/触发器/摘要策略/审计）为平台自持实现，自动压缩开关与手动压缩服务语义不变；上下文提供器为平台抽象。Microsoft.Extensions.AI 仍作为工具（AIFunction）与摘要客户端的底层抽象保留；OpenAI Responses/Anthropic 摘要客户端由 ModelBindingChatClient 绑定模型（原 MAF 的 stored-output 禁用扩展随包移除）。

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
docker build -f Backend/src/OpenAgent.Engine.Host/Dockerfile --target runtime-tests .
```

SDK 控制台继续只有 SDK 引用，外部连接入口见 [控制台使用](acp-dotnet-sdk/USAGE.md#opencode)。所有测试事实、镜像验证和未验收范围见 [本轮记录](../test-reports/2026-10-19-opencode-runtime.md)。

官方参考：[ACP 接入](https://docs.opencode.ai/docs/acp/)、[配置](https://docs.opencode.ai/docs/config/)、[Provider](https://docs.opencode.ai/docs/providers/)、[权限](https://docs.opencode.ai/docs/permissions/)。配置宏的解析顺序另核对了 [1.18.35 配置源码](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/config/config.ts)。官网可能先于固定版本变化，验收以实际 1.18.35 二进制为准。
