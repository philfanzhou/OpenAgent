# Runtime adapter 与 ACP 预览

状态：ACP v1 预览实现，保留 MAF 默认运行时。Agent 可以选择服务器登记的 ACP harness；支持本地 Claude ACP bridge、已安装 Gemini/OpenCode 的启动配置和自定义命令；容器版保留固定 OpenCode v1.19.0 与无模型协议演示。

## 绑定与扩展

Agent 配置新增纯契约字段：

```json
{
  "runtime": { "adapter": "acp", "harness": "opencode" }
}
```

旧配置默认 `{ "adapter": "maf" }`。配置持久化为 agent_configurations 的 RuntimeJson JSONB 列，新迁移用 `{}` 为旧记录填充默认 MAF。见 [配置存储](../database/tables/Configurations.md)。

`IRuntimeAdapter` 接收已解析的 RuntimeInvocation，包含请求、授权后的 Agent 配置、使用平台模型时已授权的 model profile、用户、轮次身份和授权文件描述符，返回现有 AgentResponse/AgentStreamEvent。协议/SDK 类型不进入 Contracts。`IRuntimeCatalog` 提供可选绑定，`GET /api/v1/agent/runtimes` 返回 adapter/harness/name、requiresLlmProfile、environmentMode 和 supportsCodeExecutionToggle，不返回命令、参数、环境变量或凭据。

AgentExecutor 保留配置解析和授权、会话归属检查、文件准备、追溯和指标，再分派至 adapter。MafRuntimeAdapter 保持原同步与流式循环；AcpRuntimeAdapter 驱动受登记的 stdio harness。增加新 adapter 可以注册 IRuntimeAdapter 实现；新 ACP harness 可以增加服务器配置，不必新增执行器。

```json
{
  "AgentRuntimes": {
    "Acp": {
      "Enabled": true,
      "LocalTrusted": true,
      "WorkspaceRoot": "/var/lib/openagent/acp",
      "TurnTimeoutSeconds": 600,
      "RequestTimeoutSeconds": 60,
      "Harnesses": [
        {
          "Id": "opencode",
          "Name": "OpenCode ACP",
          "Command": "/usr/local/bin/opencode",
          "Arguments": ["acp"],
          "OpenCode": true,
          "PermissionPolicy": "deny"
        },
        {
          "Id": "another-harness",
          "Name": "Another ACP harness",
          "Command": "/usr/local/bin/another-agent",
          "Arguments": ["--acp"],
          "EnvironmentMode": "local",
          "PermissionPolicy": "deny"
        }
      ]
    }
  }
}
```

- Command/Arguments/Environment 只接受服务器配置，Agent 和聊天请求不能提交执行命令。参数用 Process.ArgumentList，不经过 shell；`{workspace}` 参数占位符替换为受控路径。
- 默认关闭 ACP。打开后只显示登记的 harness；未知 adapter/harness 直接失败，不回退 MAF。
- 默认 EnvironmentMode=isolated，进程仅继承必要的 PATH/系统临时目录与隔离 HOME/XDG。LocalTrusted=true 允许 EnvironmentMode=local，保留宿主 HOME/XDG/CLAUDE_CONFIG_DIR，使用本机已登录的工具；InheritedEnvironmentVariables 明确列出允许继承的其他变量，不复制所有 Engine 环境。Environment 可用于服务器密钥配置源注入，客户端不能改启动环境。WorkingDirectory 可绑定服务器指定的绝对项目目录，仅在 LocalTrusted 下启用。
- OpenCode 将已授权的平台 provider/endpoint/key/model、指令、temperature、steps 编译进进程环境配置。真实 key 不写入消息或应用日志。ChatCompletions/Responses/Anthropic 使用各自 SDK 映射；容器预览验证 ChatCompletions；本地 harness 可直接使用自己的模型与登录配置。
- 通用 harness 只要求 ACP v1 和 stdio。默认由 harness 管理模型/认证，不查平台模型、不传平台 API key；未提供 Model 时保留原生默认模型，模型配置 API 是可选能力。Model 可指定原生模型；Claude 通过 ModelEnvironmentVariable=ANTHROPIC_MODEL 在启动时选择，其他实现按协商后的 configOptions.model 或 models 选择。UsePlatformModel=true 才启用平台模型绑定及 ModelIdTemplate；OpenCode=true 是显式的平台 OpenCode 配置转换。通用 harness 的指令作为文本上下文送入，不保证系统消息语义；steps 等专有配置不自动映射。认证、命令行参数和额外适配以该 harness 文档为准，不声称任意 ACP 实现零配置互通。
- PermissionPolicy 为 deny（默认）或 allow_once（显式服务器许可）。不支持交互审批界面，不声明客户端 fs/terminal/elicitation 能力；未知反向 RPC 返回 method-not-found。

## 会话与生命周期

会话固定 adapter/harness，旧无标记会话属于 MAF。更改绑定后必须创建新会话，不能静默切换已有推理状态。

Acp 工作目录由 tenant/user/conversation/harness 的哈希确定；HOME/XDG 数据也位于持久目录。数据库用户消息的 Metadata.Extensions 保存 runtime.adapter、runtime.harness 和 runtime.session。会话锁覆盖握手、执行及落库。每轮启动进程，initialize 后新建或 load 既有 session；load 回放不写入当前轮历史。

session ID 在发送 prompt 前随用户消息落库。续聊要求 loadSession；状态丢失/不支持加载时返回错误，不能新建 session 假装续聊。节点需要持久卷和会话存储亲和：单个 Engine 的本地目录不保证跨节点恢复。删除会话与 worker 数据 TTL 回收尚未实现。

文本、思考、工具输入/结果与计划转换为现有 SSE；最终 usage 取 harness 返回值，仅用于展示，不作为完整计费。工具 name 缺失时 title 作为展示标签，不作权限标识。平台存储保存部分内容以及完成/失败/取消状态。

取消与停止读取流时发送 session/cancel，结束整个子进程树。请求/轮次设置超时；stdout 按换行 JSON-RPC 解析并限制单帧 1,048,576 字符，通知队列有界。stderr 被持续排空但不记录，以免 harness 输出凭据/prompt。没有自动重发 prompt；失败后的外部副作用需用户确认状态。

## 首版能力边界

预览支持 harness 自带能力，**尚未提供平台 MCP/Skill/RAG/文件桥接**。配置这些平台绑定或附件时明确拒绝，避免静默丢失能力。MCP Server 参数暂为空数组；以后通过准备/桥接层增加，不能把平台工具通知当作可注册的 C# 工具。

OpenCode 的 CodeExecution 开关控制原生 bash；task/subagent、skill 和外部目录访问在此预览配置中关闭。平台手动压缩端点拒绝 ACP 会话，可在 OpenCode 使用原生命令。没有模型网关完整调用审计/硬预算，也没有用户交互审批。

harness 与 Engine 运行在同一执行宿主：容器版使用容器进程，本地版使用宿主进程。哈希目录不是 OS 沙箱，local 模式是可信本地工具模式，共享宿主原生账号，不能作为多租户凭据隔离。本预览使用独立数据库和本地端口，只适用于可信用户测试。生产启用前需要把 harness 迁到受隔离 worker，并补充网络/资源限制、动态授权、数据回收与多节点恢复。

## 构建并测试预览

需要 Docker、Python 3 和可访问 GitHub/NuGet/npm 的构建网络。

```bash
bash scripts/acp-preview.sh up
bash scripts/acp-preview.sh test
bash scripts/acp-preview.sh status
bash scripts/acp-preview.sh down
```

容器版默认页面 `http://localhost:8176`，Engine `http://localhost:5276`，Router `http://localhost:5076`。可用 ACP_CHAT_PORT/ACP_ENGINE_PORT/ACP_ROUTER_PORT 修改端口，ACP_PREVIEW_PROJECT 修改项目名。端口冲突时选择新端口，不停止其他项目。

该 compose 独立创建 Postgres/Redis 和持久 ACP 卷，不复用主实例数据；down 保留卷。镜像固定并校验 OpenCode v1.19.0 的 arm64/amd64 release digest。

可运行 `python3 scripts/package-acp-preview.py <输出目录>` 导出已经构建的镜像包。解压后运行 `bash start.sh`；包只包含镜像和启动脚本，不包含容器、数据库卷或用户数据。镜像架构取决于 Docker 构建主机，跨架构构建/测试应在对应 Docker 平台运行。追加 --local 可同时打包本地模式（需要先完成本地模式的构建）；解压后使用 start-local.sh。包内包含干净发布的宿主 Engine、Router/Chat 镜像和脚本，无需源码构建；仍需要 .NET 8、Node.js >=22 和本机已经配置的工具，Claude bridge 首次从 npm 安装。输出目录必须为空，运行中的日志、工作区、原生登录和数据库不会进入包。本 PR 沿用仓库现有 CI，不自动发布镜像包。

up 会创建两个测试 Agent：

| Agent | 实际运行时 | 模型 |
|---|---|---|
| acp-demo | Python 确定性 ACP harness | 不调用模型；用于验证协议 |
| acp-opencode | 真实 OpenCode ACP | 默认本地确定性 mock model，不代表真实 AI 任务质量 |

使用内置开发账号 `admin/admin` 或 `test/test` 登录。页面连接 Engine 或 Router，选择 ACP 测试 Agent；仅 acp-opencode 需要选择 `ACP preview local model (no AI)`。demo 无需平台模型。发送普通消息测试续聊；demo 的 `/permission`、`/plan`、`/slow` 用于工具/计划/停止测试；OpenCode 的 `test tool` 触发 mock model 返回真实 bash 工具调用（执行 pwd）。

测试真实模型时，在页面模型设置创建自己的模型连接并选择它，然后新建 acp-opencode 会话。不要对 mock model 做“质量/推理能力”评估。模型凭据保存在预览平台原有加密配置中，预览不读取主实例凭据。

## 本机多 harness 预览（推荐测试 Claude）

参考 [Paperclip Claude local 的 ACP 启动配置](https://github.com/paperclipai/paperclip/blob/master/packages/adapters/claude-local/src/server/acp.ts)：共用协议与生命周期引擎，每个 harness 分别配置启动命令、模型和环境。Paperclip 代码仅用作架构参考，本实现没有复制其执行器。

```bash
python3 scripts/acp-local-preview.py up
python3 scripts/acp-local-preview.py test
python3 scripts/acp-local-preview.py test --real
python3 scripts/acp-local-preview.py status
python3 scripts/acp-local-preview.py down
```

需要本机 .NET 8、Node.js >=22、Python 3、Docker，以及已安装/配置好的本地工具。up 构建并在宿主启动 Engine，仅数据库、Redis、Router、Chat 放在 Docker。使用独立项目 openagent-acp-local 和数据库卷；不把本机 HOME、登录信息或源码挂入容器。日志/PID/发布文件保存于忽略的 .preview/acp-local；down 保留预览数据。

页面 `http://localhost:8177`，Engine `http://localhost:5277`，Router `http://localhost:5077`。数据库 5477、Redis 6387，仅发布到本机。ACP_LOCAL_CHAT_PORT/ENGINE_PORT/ROUTER_PORT/DB_PORT/REDIS_PORT 可分别覆盖；ACP_LOCAL_PROJECT 可替换项目名。本地容器 Router 通过 host.docker.internal 访问宿主 Engine，此路径在 macOS Docker Desktop 完成验证；其他宿主需验证连通性。

脚本按本机安装情况登记：

| Harness | ACP 启动方式 | 认证与模型 |
|---|---|---|
| Claude Code | 固定 @agentclientprotocol/claude-agent-acp@0.89.1 bridge + 本机 Claude CLI | 保留本机登录/用户配置，无需平台 OpenAI key；up 自动安装 bridge 到 .preview/claude-acp |
| Gemini CLI | 已安装 gemini --experimental-acp | 本机 Gemini 配置；未安装时不登记 |
| OpenCode local | 已安装 opencode acp（不启用平台 OpenCode 转换） | 本机 provider 配置；未安装时不登记 |
| 自定义 ACP | ACP_LOCAL_HARNESSES_FILE 指向 JSON harness 数组 | 服务器配置的 command/args/environment/model；CLI 需要真正支持 ACP，普通交互 CLI 需 wrapper |
| protocol demo | Python 确定性协议测试进程，无模型配置 API | 无模型，无凭据 |

本机 Claude 的最小服务器配置示例（路径替换为实际安装位置）：

```json
{
  "Id": "claude-local",
  "Name": "Claude Code (local ACP)",
  "Command": "/absolute/path/to/node",
  "Arguments": ["/absolute/path/to/claude-agent-acp/dist/index.js"],
  "EnvironmentMode": "local",
  "Environment": { "CLAUDE_CODE_EXECUTABLE": "/absolute/path/to/claude" },
  "ModelEnvironmentVariable": "ANTHROPIC_MODEL",
  "PermissionPolicy": "allow_once"
}
```

模型可不设，保留本机默认；可通过 Model 指定覆盖。变量继承名单可加 ANTHROPIC_BASE_URL、ANTHROPIC_API_KEY 等，需要明确的服务器配置，不能从聊天请求提交。原生 MCP/Skills 是否可用由 harness 自己的本机设置决定；平台绑定仍按上面的首版边界执行。

用开发账号 admin/admin 登录，选择 **Claude Code (local ACP)**，顶部显示“运行时管理模型与登录”；不需要创建或选择平台模型。Agent 设置里的“测试运行时”只检查命令启动、ACP 握手和 session/new，展示失败阶段；不声称验证模型配额或真实工具。test --real 才实际调用本机 Claude 当前模型，检查文本、续聊、Bash 工具/权限和 Router 转发。本次真实验证使用本机 Claude 配置的 glm-5.3-flash[1M]，不是 Anthropic 模型质量测试。Gemini/OpenCode local 未安装，未进行真实验证。

当出现 `API key is required for OpenAI` 时先检查所选 Agent 和会话的 runtime：该异常来自平台模型客户端，原生 ACP harness 不创建这个客户端。MAF 的平台模型仍必须有有效连接。切换 ACP Agent 后请新建对话，已有会话不能修改执行状态绑定。

## 验证入口与源码

- `Backend/src/OpenAgent.Contracts/Runtime/IRuntimeAdapter.cs`：公共执行扩展契约。
- `Backend/src/OpenAgent.Core/Execution/RuntimeRegistry.cs`：adapter/harness 注册与选择。
- `Backend/src/OpenAgent.Core/Execution/AcpRuntimeAdapter.cs`：ACP 生命周期。
- `Backend/src/OpenAgent.Core/Execution/AcpConnection.cs`：双向 JSON-RPC 与进程。
- `Backend/src/OpenAgent.Core/Conversation/ExternalRuntimeTurn.cs`：会话锁、绑定和事件落库。
- `Backend/tests/OpenAgent.Core.Tests/Execution/AcpRuntimeAdapterTests.cs`：无服务依赖的真实子进程协议测试（需要 python3）。
- `scripts/test-acp-preview.py`：HTTP/SSE、配置持久化、真实 OpenCode 和本地 mock model 闭环。

设计研究及完整业务能力迁移路径见 [可行性调研](./opencode-acp.md)。
