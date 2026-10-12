# Runtime adapter 与 ACP 预览

状态：ACP v1 预览实现，保留 MAF 默认运行时。Agent 可以选择服务器登记的 ACP harness；本次提供 OpenCode v1.19.0 和无模型调用的协议演示 harness。

## 绑定与扩展

Agent 配置新增纯契约字段：

```json
{
  "runtime": { "adapter": "acp", "harness": "opencode" }
}
```

旧配置默认 `{ "adapter": "maf" }`。配置持久化为 agent_configurations 的 RuntimeJson JSONB 列，新迁移用 `{}` 为旧记录填充默认 MAF。见 [配置存储](../database/tables/Configurations.md)。

`IRuntimeAdapter` 接收已解析的 RuntimeInvocation，包含请求、授权后的 Agent/model profile、用户、轮次身份和授权文件描述符，返回现有 AgentResponse/AgentStreamEvent。协议/SDK 类型不进入 Contracts。`IRuntimeCatalog` 提供可选绑定，`GET /api/v1/agent/runtimes` 返回 adapter/harness/name，不返回命令、参数、环境变量或凭据。

AgentExecutor 保留配置解析和授权、会话归属检查、文件准备、追溯和指标，再分派至 adapter。MafRuntimeAdapter 保持原同步与流式循环；AcpRuntimeAdapter 驱动受登记的 stdio harness。增加新 adapter 可以注册 IRuntimeAdapter 实现；新 ACP harness 可以增加服务器配置，不必新增执行器。

```json
{
  "AgentRuntimes": {
    "Acp": {
      "Enabled": true,
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
          "ModelIdTemplate": "{model}",
          "PermissionPolicy": "deny"
        }
      ]
    }
  }
}
```

- Command/Arguments/Environment 只接受服务器配置，Agent 和聊天请求不能提交执行命令。参数用 Process.ArgumentList，不经过 shell；`{workspace}` 参数占位符替换为受控路径。
- 默认关闭 ACP。打开后只显示登记的 harness；未知 adapter/harness 直接失败，不回退 MAF。
- 进程默认不继承 Engine 环境，仅传入必要的 PATH/系统临时目录与隔离 HOME/XDG 路径。Environment 可用于服务器配置的原生认证，应由密钥配置源注入。
- OpenCode 将已授权的平台 provider/endpoint/key/model、指令、temperature、steps 编译进进程环境配置。真实 key 不写入消息或应用日志。ChatCompletions/Responses/Anthropic 使用各自 SDK 映射；本次真实预览验证的是 ChatCompletions。
- 通用 harness 需要支持 ACP v1、stdio 和模型配置选项（configOptions.model 或旧 models）。ModelIdTemplate 把平台 provider/model 映射到 harness 可选模型 ID。通用 harness 的指令作为文本上下文送入，不保证系统消息语义；steps 等专有配置不自动映射。认证、命令行参数和额外适配以该 harness 文档为准，不声称任意 ACP 实现零配置互通。
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

当前进程与 Engine 位于同一容器，哈希目录不是 OS 沙箱。本预览使用独立数据库和本地端口，只适用于可信用户测试。生产启用前需要把 harness 迁到受隔离 worker，并补充网络/资源限制、动态授权、数据回收与多节点恢复。

## 构建并测试预览

需要 Docker、Python 3 和可访问 GitHub/NuGet/npm 的构建网络。

```bash
bash scripts/acp-preview.sh up
bash scripts/acp-preview.sh test
bash scripts/acp-preview.sh status
bash scripts/acp-preview.sh down
```

默认页面 `http://localhost:8176`，Engine `http://localhost:5276`，Router `http://localhost:5076`。可用 ACP_CHAT_PORT/ACP_ENGINE_PORT/ACP_ROUTER_PORT 修改端口，ACP_PREVIEW_PROJECT 修改项目名。端口冲突时选择新端口，不停止其他项目。

该 compose 独立创建 Postgres/Redis 和持久 ACP 卷，不复用主实例数据；down 保留卷。镜像固定并校验 OpenCode v1.19.0 的 arm64/amd64 release digest。

可运行 `python3 scripts/package-acp-preview.py <输出目录>` 导出已经构建的镜像包。解压后运行 `bash start.sh`；包只包含镜像和启动脚本，不包含容器、数据库卷或用户数据。镜像架构取决于 Docker 构建主机，跨架构构建/测试应在对应 Docker 平台运行。本 PR 沿用仓库现有 CI，不自动发布镜像包。

up 会创建两个测试 Agent：

| Agent | 实际运行时 | 模型 |
|---|---|---|
| acp-demo | Python 确定性 ACP harness | 不调用模型；用于验证协议 |
| acp-opencode | 真实 OpenCode ACP | 默认本地确定性 mock model，不代表真实 AI 任务质量 |

使用内置开发账号 `admin/admin` 或 `test/test` 登录。页面连接 Engine 或 Router，选择测试 Agent 和 `ACP preview local model (no AI)`。发送普通消息测试续聊；demo 的 `/permission`、`/plan`、`/slow` 用于工具/计划/停止测试；OpenCode 的 `test tool` 触发 mock model 返回真实 bash 工具调用（执行 pwd）。

测试真实模型时，在页面模型设置创建自己的模型连接并选择它，然后新建 acp-opencode 会话。不要对 mock model 做“质量/推理能力”评估。模型凭据保存在预览平台原有加密配置中，预览不读取主实例凭据。

## 验证入口与源码

- `Backend/src/OpenAgent.Contracts/Runtime/IRuntimeAdapter.cs`：公共执行扩展契约。
- `Backend/src/OpenAgent.Core/Execution/RuntimeRegistry.cs`：adapter/harness 注册与选择。
- `Backend/src/OpenAgent.Core/Execution/AcpRuntimeAdapter.cs`：ACP 生命周期。
- `Backend/src/OpenAgent.Core/Execution/AcpConnection.cs`：双向 JSON-RPC 与进程。
- `Backend/src/OpenAgent.Core/Conversation/ExternalRuntimeTurn.cs`：会话锁、绑定和事件落库。
- `Backend/tests/OpenAgent.Core.Tests/Execution/AcpRuntimeAdapterTests.cs`：无服务依赖的真实子进程协议测试（需要 python3）。
- `scripts/test-acp-preview.py`：HTTP/SSE、配置持久化、真实 OpenCode 和本地 mock model 闭环。

设计研究及完整业务能力迁移路径见 [可行性调研](./opencode-acp.md)。
