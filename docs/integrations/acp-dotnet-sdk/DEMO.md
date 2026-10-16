# C# ACP 最小 Demo（历史基线）

当前正式开发入口已切换到单包 SDK，长期测试入口见 [测试控制台](./TEST-CONSOLE.md)。本页保留旧 Demo 的运行与实测说明，不约束新 SDK 目录，也不计入新 SDK 完整能力验收。

这是一个 .NET 8 控制台示例，包含 stdio Agent 和自动启动它的 Client。Agent 使用本地 Echo，无需模型密钥、数据库、容器或其他运行时依赖；测试复用仓库已有的 xUnit 包版本。

源码：[示例解决方案](../../../Backend/samples/acp-demo/AcpDemo.sln)、[Agent](../../../Backend/samples/acp-demo/OpenAgent.Acp.Demo/AcpAgent.cs)、[Client](../../../Backend/samples/acp-demo/OpenAgent.Acp.Demo/DemoClient.cs)、[客户端分派](../../../Backend/samples/acp-demo/OpenAgent.Acp.Demo/ClientConnection.cs)、[传输](../../../Backend/samples/acp-demo/OpenAgent.Acp.Demo/JsonLineTransport.cs)。

## 直接运行

在仓库根目录依次执行，先完成构建，再运行示例：

```sh
dotnet build Backend/samples/acp-demo/AcpDemo.sln
dotnet run --no-build --project Backend/samples/acp-demo/OpenAgent.Acp.Demo -- client "你好 ACP 🥐"
```

Client 会自动启动同一个程序的 `agent` 模式，完成握手、创建会话、发送 prompt，再逐段显示回复：

```text
Initialized ACP v1.
Agent: dotnet-echo-demo (0.1.0)
Session: <generated session ID>
Echo: 你好 ACP 🥐
Stop reason: end_turn
```

取消示例在收到第一个文本片段后发送取消，等待 Agent 确认：

```sh
dotnet run --no-build --project Backend/samples/acp-demo/OpenAgent.Acp.Demo -- client --cancel "Cancel this streaming response"
```

末行应为 `Stop reason: cancelled`。取消前显示多少文本由调度时序决定；Client 不把“已发送取消”直接当作“已取消成功”。演示客户端默认有 30 秒总超时，可通过 `--timeout-seconds` 修改；退出时关闭 stdin，必要时在 5 秒宽限后终止自己启动的子进程。

## 连接本地外部 Agent

Client 可以启动任意本地 ACP stdio Agent。使用 `--agent` 指定可执行程序、重复 `--agent-arg` 逐项传参；参数通过进程 API 传递，不由 shell 拼接解释。

例如，已安装 Claude ACP 适配器时，先创建一个临时空工作目录，再运行（替换示例绝对路径）：

```sh
dotnet run --no-build --project Backend/samples/acp-demo/OpenAgent.Acp.Demo -- client \
  --agent node \
  --agent-arg /absolute/path/to/claude-agent-acp/dist/index.js \
  --cwd /absolute/path/to/empty-test-directory \
  --timeout-seconds 120 \
  -- "Without using tools or accessing files, calculate 19 + 23 and reply with exactly: ACP_LOCAL_OK 42"
```

Claude ACP 适配器使用本机已有认证；Demo 不读取或保存凭据。需要指定本机 Claude CLI 时，可在启动环境设置 `CLAUDE_CODE_EXECUTABLE`。用于简短连通性检查时，可设置 `CLAUDE_CODE_SAFE_MODE=1` 关闭自定义插件等。不要把 CLI 的普通文本模式当成 ACP Agent，必须使用它提供的 ACP 入口或适配器。

`--cwd` 同时用于启动进程和 session/new；`--timeout-seconds` 范围为 1–1800。默认不带 `--agent` 时仍启动 Echo Agent。程序会显示 Agent 信息，以及对端通过 configOptions 报告的模型选择值；该值可能是 `default` 等别名，不能据此断言供应商后台的实际模型路由。

真实 Agent 可在会话响应前发送通知或反向权限请求，Client 会持续分派后再等待对应响应。可选能力保持空，权限默认取消，其他未支持反向请求返回 -32601。

## 支持范围

| 方向/方法 | 已实现行为 |
|---|---|
| Client → Agent `initialize` | 返回协议版本 1、Agent 信息、空可选能力与 authMethods |
| Client → Agent `session/new` | 校验绝对 cwd 和空 mcpServers，分配内存 sessionId，最多 32 个会话 |
| Client → Agent `session/prompt` | 接受 text/resource_link，输出 Echo；同一会话同时仅一轮，超出返回 10001 |
| Agent → Client `session/update` | 按 Unicode rune 流式发送 agent_message_chunk，支持中文、换行和表情 |
| Client → Agent `session/cancel` | 通知无独立响应；原 prompt 返回 cancelled；会话可继续对话 |
| Agent → Client `session/request_permission` | 演示 Client 默认回复 cancelled，不自动授权；Echo Agent 不发起工具请求 |
| JSON-RPC 错误 | 解析/请求/参数错误、未知方法、会话不存在；保留字符串、int64、null 请求 ID |
| stdio | UTF-8 NDJSON、CRLF、分片/合包、单条消息 1 MiB 上限；Agent 日志只到 stderr |

未实现会话持久化、load/resume、文件/终端工具、MCP 连接、认证流程、通用请求取消、batch、HTTP/WebSocket 或 AOT。非空 mcpServers 被明确拒绝；resource_link 仅回显 URI，不下载资源。因此它是用于理解和扩展基础流程的 Demo，不能作为完整 ACP v1 合规 SDK 发布。

## 接入其他 Client

先构建，再将宿主的 Agent 启动程序配置为 `dotnet`，参数依次传入构建产物的**绝对路径**和 `agent`：

```text
dotnet /absolute/path/to/OpenAgent.Acp.Demo.dll agent
```

Agent 模式的 stdout 只包含协议消息。不要把 `dotnet run` 的构建日志接到协议通道；宿主启动配置使用已构建 DLL。

示例 wire 消息如下，每条单独占一行。先初始化，再新建会话；将后续消息中的 SESSION_ID 换成 session/new 返回值：

```json
{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}
{"jsonrpc":"2.0","id":"new","method":"session/new","params":{"cwd":"/absolute/workspace","mcpServers":[]}}
{"jsonrpc":"2.0","id":"prompt","method":"session/prompt","params":{"sessionId":"SESSION_ID","prompt":[{"type":"text","text":"Hello ACP"}]}}
{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"SESSION_ID"}}
```

不要一次性把这四行当作普通输入文件立即关闭 stdin：EOF 表示断开连接，会取消仍在执行的工作。实际 Client 需要保持输入打开并读取异步输出。

## 验证与下一步

```sh
dotnet test Backend/samples/acp-demo/AcpDemo.sln --no-build
```

测试包含独立原始 JSON Client 对 Echo Agent 子进程的验证、传输边界，以及 Client 的通知/反向请求分派等用例。初版结果见 [Demo 验证记录](../../test-reports/2026-10-15-acp-demo.md)；34 个用例的回归与本地 Claude ACP 实测见 [本地 Agent 验证](../../test-reports/2026-10-16-acp-local-agent.md)。本例独立于平台主解决方案，没有修改 OpenAgent 服务依赖或对外接口。

向 SDK 演进时，先提取协议 DTO、双向 RPC 和 session 生命周期，再按 [SPEC.md](./SPEC.md) 扩展；不要将本例的原始 JsonElement 分派器误认为已经完成规格中的类型化 SDK。
