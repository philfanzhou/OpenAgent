# ACP 测试控制台

> v0.3 · 2026-10-16。工程：`Backend/tools/OpenAgent.Runtime.Acp.TestConsole`。它是长期测试入口，随着 SDK 能力同步扩展，不是一次性 Demo，也不是第二套协议实现。

## 1. 当前可运行命令

在仓库根目录先构建，再执行：

```sh
dotnet build Backend/OpenAgent.sln
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- list
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run framing
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run all
dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests --no-build
```

当前仅 `framing` 已实现：调用 SDK 的 Stream transport，检查 Unicode、CRLF、多帧读取、EOF 和写入。成功输出 `PASS framing (SDK transport; no Agent or model invoked)`；这不表示已运行完整 ACP、真实 Agent 或模型。`run all` 只包含已注册的离线场景。

退出码：0=所选已实现场景通过或帮助/列表正常结束；1=场景失败；2=未知命令/场景或参数错误。未知功能不能静默跳过后返回成功。

自动化用例启动真实控制台进程检查 list、单场景、all 和错误参数；SDK 仍由对应单元/契约测试独立验证。

## 2. 后续命令与场景要求

下表为后续交付目标，不能当成当前命令已可用；阶段及子任务见 IMPLEMENTATION.md。

| 阶段 | 新场景/模式 | 验证内容 |
|---|---|---|
| P1 | schema、结构化报告 | 固定 schema、序列化边界、覆盖清单 |
| P2 | rpc / reverse / fault | 双向嵌套、错误、取消、EOF、配额与调度 |
| P3 | agent echo / client、echo / cancel | 强类型 SDK 的 stdio 双侧闭环与流式取消 |
| P4 | 外部 Agent 启动选项、process | 参数、cwd、stderr、退出码、所有权及清理 |
| P5 | session / config / updates | 恢复、回放、分页、配置和完整更新 |
| P6 | permission / files / terminal / elicitation / auth | 正常/拒绝/取消和资源授权边界 |
| P7 | interop / load / AOT 调用方式 | 固定对端、负载、发布产物与平台报告 |

新增能力时同步注册场景、帮助/列表、run all、测试和文档。一个场景对应一个主要能力，可由进程参数选择边界条件；不要逐步把所有逻辑堆进 Program。

## 3. 执行与结果契约

- SDK 提供协议行为；控制台仅做参数解析、注册 handler、调 SDK、展示结果及断言场景。UI 模拟、Echo 存储和测试权限策略留控制台，不进入通用 SDK。
- 离线场景可重复运行，使用确定性 fake handler、临时目录与自有进程；用例结束回收。集成测试同时验证故障退出和资源释放。
- `agent` 模式的 stdout 必须是纯 ACP，日志走 stderr；client/场景模式可以展示人类可读结果。
- P1 起的结构化报告包含场景/阶段、PASS/FAIL/SKIPPED/NOT_TESTED、耗时、协议/能力、对端版本及脱敏错误。当前文本模式不假装具备该格式。
- run all 的汇总只能声明本次执行的已实现场景通过，不表示未来阶段完成；完整发布验收还需覆盖率清单与互操作矩阵。
- 真实 Agent/模型、交互 URL 打开、真实认证均为显式选项；离线 all 不连接真实账户、不执行 logout、不访问个人仓库。普通 CLI 文本模式不能当作 ACP 入口。
- 不记录密钥、环境变量值、完整 prompt/工具结果；transcript 必须显式开启、脱敏并限定保存位置。
- 每个进程测试设置期限，超时回收自己启动的进程；收到取消不能直接显示远端 cancelled，必须等待协议确认。

控制台自身不是测试框架替代品：新增任何 SDK 功能时，自动化测试和对应控制台场景必须同时可运行，任务才可进入验收。
