# SDK 测试控制台

工程 `Backend/tools/OpenAgent.Runtime.Acp.TestConsole` 长期随 SDK 演进，只调用公共 API。

```sh
dotnet build Backend/OpenAgent.sln
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- list
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run framing
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run all
dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests --no-build
```

当前仅 framing：Unicode、CRLF、多帧、EOF、写入；PASS 只表示此场景通过，不代表 Agent/模型或完整 SDK。退出码 0=成功/帮助/列表，1=场景失败，2=参数/场景错误；未知功能不得静默跳过。

新增能力同时更新 list/help、run all、测试和文档；未来场景按 [IMPLEMENTATION](./IMPLEMENTATION.md) 增加，未实现命令不能当作可用功能。测试必须启动真实控制台，检查输出/退出码、超时、回收自有进程；不能仅测试解析器。

控制台只做参数、handler、展示、断言和报告，不复制协议循环。UI/内存示例留控制台；agent stdout 只写协议、诊断走 stderr。默认 all 仅确定性离线场景，使用临时目录、自有进程和假身份；真实 Agent/模型/账户、URL 打开须显式启用，不进入 all，不执行真实 logout。

P1 起结构化报告包含场景/阶段、PASS/FAIL/SKIPPED/NOT_TESTED、耗时、协议/能力、对端版本与脱敏错误；负载报告另附原始参数/指标。N/A 说明可选能力不适用，不能替代本方声明能力的验收。transcript 显式启用并脱敏；取消须区分已发送和远端确认。
