# OpenAgent.Runtime.Acp 使用指南

单一 ACP v1 SDK，当前目标为 .NET 10。包内包含 Protocol、Transport、Rpc、Client、Agent、Processes，命名空间不等于独立 NuGet 包。

## Client

```csharp
await using AcpProcess process = AcpProcess.Start(new("your-agent", ["acp"]));
await using AcpClient client = new(process.Transport);
await client.InitializeAsync();
await using AcpSession session = await client.NewSessionAsync(new(absoluteWorkspace, []));
Task updates = ConsumeAsync(session); // 并发消费 ReadUpdatesAsync，不能等 prompt 结束才开始
await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("你好")]);
PromptResponse result = await turn.Completion;
await session.DisposeAsync();
await updates;
```

示例使用 `OpenAgent.Runtime.Acp.{Client,Processes,Protocol}` 命名空间。`ConsumeAsync` 由宿主实现，使用 `await foreach (var update in session.ReadUpdatesAsync())`。Session 只允许一个消费者；prompt 取消使用 `CancelAndWaitAsync` 或传入取消 token。`StopReason` 来自原始响应，连接故障通过异常报告。

`LoadSessionAsync` 会接收历史回放，`ResumeSessionAsync` 只恢复会话，`ListSessionsAsync` 支持 cwd/cursor，`DeleteSessionAsync` 与关闭会话分离。可选方法先验证协商能力。`SetModeAsync` / `SetConfigAsync` 接受已公布的配置；select 校验选项，boolean 发送对应 type。

## Agent 与交互

使用 `AcpAgentHost(transport, handlers)`。`AcpAgentHandlers` 必填 Initialize、NewSession、Prompt 三个委托，其他生命周期按需注册。Prompt 回调通过 `AcpAgentContext` 发送更新及反向权限/文件/终端/elicitation 请求。基于内存连接的完整可运行用例见 `Backend/tests/OpenAgent.Runtime.Acp.Tests/Client/`（`AcpAgentHost` 与回调的真实用法）与同目录 `TestDoubles/`；示例只使用内存，不调用模型。

Client 通过 `AcpClientHandlers` 注册 Permission、ReadFile、WriteFile、Terminal、Elicitation、TerminalAuthentication。文件/命令默认不可用，权限默认拒绝。`RootedFileSystem`、`LocalTerminalHandler` 是显式启用的示例策略，宿主应提供本会话授权根和命令判断；不能将其当作沙箱或跨租户鉴权实现。URL 流程只分派给宿主，不自动打开浏览器；terminal-auth 不自动执行未授权命令。

真实 Agent 由宿主经 `AcpProcess` 显式启动并自行授权；官方协议测试不需要模型账户，不能据此宣称真实模型推理或平台 E2E 已验收。
## OpenCode

已安装并配置 OpenCode 的开发环境可用 SDK 客户端对接 `opencode acp` 子进程验证协议互通。平台采用独立环境、授权后的模型快照和每轮 MCP 桥，使用方式及无需付费模型的真实进程测试见 [OpenCode 运行时](../opencode-runtime.md)。
