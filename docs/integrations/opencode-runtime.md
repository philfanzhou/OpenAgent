# OpenCode ACP 聊天运行时

本分支将聊天默认执行入口切换为 OpenCode，依赖独立的 [ACP SDK](acp-dotnet-sdk/README.md)。用户明确选择 OpenCode；本地互操作固定为 **1.18.35**。实现状态与 PR 顺序只维护在 [任务清单](../planning/TODO.md)，验证事实见 [本轮记录](../test-reports/2026-10-19-opencode-runtime.md)。

## 架构与目录

```text
Backend/src/
  OpenAgent.Contracts/Runtime/IAgentRuntimeDriver.cs  平台输入、事件和工具回调契约
  OpenAgent.Application/Execution/
    AgentExecutor.cs               既有公开入口
    RuntimeAgentExecutor.cs        身份/配置/文件/持久化协调
    RuntimePreparation.cs          能力、MCP、Skill、历史准备
    RuntimePreparationScope.cs     部分结果和资源生命周期
    ContextOnlyChatClient.cs       兼容 Provider 的载体，禁止推理
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
2. 平台把历史和能力、外部 MCP、Skill Provider 准备成 RuntimeInvocation。工具函数带本轮闭包；每次回调校验 Tool/Function 的 invoke 权限，沿用工具超时、独占/只读并发和结果字符预算。各资源域的细粒度授权仍见 F04 待办，不把桥接令牌当作完整权限系统。
3. 驱动创建随机工作区、独立 HOME/XDG 状态目录和 loopback MCP 桥。桥使用 256 bit 随机 bearer，身份不取自模型参数，整轮取消后拒绝新请求。
4. 通过 SDK 启动 `opencode acp`，执行 initialize → session/new → 可用时选择 model config → session/prompt。OpenCode 直接调用选定的模型，并经 MCP 桥调用平台 C# 工具；平台不再执行 MAF 聊天推理循环。
5. 消费 session/update，合并工具增量、保留调用 ID、去除桥的显示名前缀，将正文、reasoning、工具结果和计划映射到 AgentStreamEvent。相同工具参数更新不重复发送；参数确有变化时沿同一个 ID 更新。
6. ACP prompt 结束后排空更新队列。平台保存会话成功后才发送 usage/done。失败或取消保存部分正文、reasoning 与工具记录。
7. 取消发送 session/cancel 并等待原 prompt 结果；SDK 对不确认的进程执行有界回收。结束后关闭 MCP 桥、释放工具资源并删除本轮随机工作区，保留平台会话数据。

OpenCode 的原生工具使用 `* = deny`，仅允许 `openagent_*` MCP 工具；Client 不启用文件/终端回调，ACP 权限请求默认拒绝。平台 Runner、文件资产和 Skill 脚本策略继续约束实际工具。这是应用层授权和进程环境隔离，**不是操作系统沙箱**；本轮不宣称解决恶意 harness 的主机访问问题，也不实现人工审批。

## 模型与历史

- 只将本轮授权后的 Endpoint、ModelId、ApiFormat、Temperature、ContextTokens 交给 OpenCode。ApiKey 用独立子进程环境变量传入，配置中只放 `{env:OPENAGENT_MODEL_API_KEY}`，不拼接到命令、提示词或日志。
- OpenCode 会在 JSON 解析前展开配置宏。驱动在序列化时转义租户文本中的花括号，只还原随机占位的密钥引用，避免 `{file:...}`/`{env:...}` 指令变成宿主文件读取或环境变量展开；有真实进程回归测试。
- 子进程只继承 PATH/SystemRoot/WINDIR 白名单，不继承服务数据库口令、云凭据或用户 OpenCode 配置。启动命令由宿主配置，聊天请求不能替换。
- ApiFormat 映射为 OpenAI Chat Completions、OpenAI Responses、Anthropic 对应的 AI SDK Provider；本轮真实本地模型验证覆盖 Chat Completions，其余两种映射尚需对应模型互操作验收。
- ContextTokens 未配置时用 128000 作为驱动配置回退值；输出上限为上下文的四分之一且不超过 16384。实际模型限制应在平台模型 profile 中配置，不把回退值当作服务商能力。
- 每轮使用新的 OpenCode 进程与 ACP session。旧消息按 role/content 序列化为一个嵌入 JSON 资源；当前 query 和平台筛选后的图片作为内容块传入。平台会话主键不变，但不沿用 OpenCode 私有会话或 ACP session/load。历史中的工具结果是上下文数据，不会自动重放执行。
- usage_update.used 是上下文占用量，不是本轮计费 token。没有可靠本轮用量时 TokenUsage 保持 null，ModelId/TraceId 仍随终态返回。

## 配置与启动

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

缺少可执行文件、启动失败或 ACP 异常会明确失败，不自动回退 MAF。外部错误正文不会原样进入 SSE/会话；日志记录异常类型。工作区清理失败会告警并保留可定位目录，不删除其他轮次或宿主根目录。

## 兼容组件与后续范围

MAF/Microsoft.Extensions.AI 暂保留为 PlatformChatHistory、Skill Provider 兼容和平台独立摘要服务。自动压缩开关沿用既有摘要预算/审计，手动压缩服务不变；摘要服务仍通过原 Provider 客户端访问模型。ContextOnlyChatClient 禁止聊天推理。原聊天执行器快照只编入两个测试程序集，生产没有第二条 MAF 聊天执行路径。

首版将经过授权的全部 MCP 工具列入本轮桥目录，不启用原 MAF DeferredToolInjector；`Mcp:DeferredToolThreshold` 不控制 OpenCode 的工具目录。工具数和 schema 大小需要按实际目录验收。OpenCode 内部可能产生标题等辅助模型请求，本轮未采集其底层 HTTP 请求/响应到平台 LLM interaction 表，也不据此伪造 usage。

Worker/分布式租约与 fencing、RunGrant、跨重启事件重放、人工审批、全量删除 MAF 包和真实服务商验收仍按任务清单单独推进。本轮没有部署或修改线上配置。需要回退时回退此平台接入 PR；独立 SDK PR 可继续保留。

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
