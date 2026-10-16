# C# ACP Client 连接本地 Agent 验证

日期：2026-10-16（Asia/Shanghai）。环境：macOS arm64、.NET SDK 8.0.422。结论：**本地 ACP 连接与会话创建通过，真实模型回复未通过，当前推理路由返回 HTTP 400。**

## 执行链与范围

```text
C# Demo Client → stdio ACP v1 → Claude ACP adapter → 本机 Claude Code → 配置的模型服务
```

- 本机 Claude Code：`2.1.301`。
- 已缓存的 `@agentclientprotocol/claude-agent-acp`：`0.89.1`，复用本机安装，没有下载或升级。
- 每次验证创建新的临时空目录与 ACP session，不读取工作仓库内容；提示要求不使用工具、不访问文件。
- 适配器沿用本机认证；本轮未写入全局模型配置或保存凭据。

## 最后一次重试

用户调整模型后重新启动 Client/适配器/会话，使用新配置。发送的测试提示要求计算 `19 + 23` 并只返回 `ACP_LOCAL_OK 42`。

| 阶段 | 结果 | 证据 |
|---|---|---|
| 启动本地 Agent | 通过 | Node 适配器及本机 Claude CLI 正常启动 |
| initialize | 通过 | 协商 `protocolVersion: 1`，返回 Claude ACP adapter 信息 |
| session/new | 通过 | 返回新的 sessionId |
| 模型选择 | 已读取新配置 | ACP configOptions 报告当前值 `opus`；它是选择值，不是底层实际模型的身份保证 |
| session/prompt | 未通过 | 服务返回 HTTP 400，提示 Stealth Ox Alpha 测试期已经结束 |
| 实际文本结果 | 未通过 | 未得到预期 `ACP_LOCAL_OK 42`；Agent 输出的 API 错误文本不视为成功推理 |
| 错误传播 | 通过 | Client 显示 RPC 错误并以退出码 1 退出，没有伪报 end_turn 成功 |

重试前，原配置 `glm-5.3-flash[1M]` 已完成 ACP 握手及会话创建，但回复超时。独立的供应商诊断请求分别观察到：该带后缀模型返回模型不存在（HTTP 400）；`glm-5.3-flash` 返回额度上限（HTTP 429）。这些是之前配置的诊断结果，不能替代本表的新配置重试结果。

当前仍需修正推理服务的可用模型/路由配置，才能验证完整真实回复。无需因此更改 ACP framing 或会话协议，也不应仅凭 `opus` 别名宣称已连接到某个实际模型。

## 为验证补充的 Demo 能力

源码与使用说明见 [DEMO.md](../integrations/acp-dotnet-sdk/DEMO.md)。

- `--agent`、重复的 `--agent-arg`、`--cwd`、`--timeout-seconds`；不通过 shell 拼接启动命令。
- 等待 initialize/session/new/prompt 响应时，持续处理通知与反向请求；权限请求默认取消。
- 显示对端 Agent 信息及模型选择值；保留远端 RPC 错误。
- 子进程 stderr 持续排空，不收集任意供应商日志或认证信息；保持退出清理。

## 自动回归

运行了 Demo 构建，以及新增 Client 测试后的测试项目构建；均为 **0 warning、0 error**。随后执行：

```sh
dotnet test Backend/samples/acp-demo/AcpDemo.sln --no-build
```

结果：**34 通过、0 失败、0 跳过**。在原 23 个用例上新增了早到通知、反向权限/不支持请求、远端错误与断线、外部启动参数解析等 11 个用例。Echo 取消演示仍返回 `cancelled`。

本报告不声称真实模型推理、真实 Agent 取消、完整 SDK、其他操作系统或 AOT 已验证成功。
