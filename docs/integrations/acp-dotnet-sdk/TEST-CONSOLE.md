# SDK 测试控制台

工程 `Backend/tools/OpenAgent.Runtime.Acp.TestConsole` 长期随 SDK 演进，只调用公共 API。

```sh
dotnet build Backend/OpenAgent.sln
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- list
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run framing
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run lifecycle
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run schema --report schema-report.json
dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- run all --report all-report.json
dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests --no-build
```

P0 场景：framing 验证 Unicode、CRLF/上限、多帧、EOF、写入；lifecycle 验证 EOF 写入排空、取消回调异常后的清理、并发释放。schema（P1）读取构建时复制的固定 contracts.json，覆盖 178 个定义、25 方法和 14 更新，校验正常往返、非法读取/发送及默认能力。all 包含三者；PASS 不代表 Agent/模型或完整 SDK。退出码 0=成功/帮助/列表，1=场景失败，2=参数/场景错误；未知功能不得静默跳过。

新增能力同时更新 list/help、run all、测试和文档；未来场景按 [IMPLEMENTATION](./IMPLEMENTATION.md) 增加，未实现命令不能当作可用功能。测试必须启动真实控制台，检查输出/退出码、超时、回收自有进程；不能仅测试解析器。

控制台只做参数、handler、展示、断言和报告，不复制协议循环。UI/内存示例留控制台；agent stdout 只写协议、诊断走 stderr。默认 all 仅确定性离线场景，使用临时目录、自有进程和假身份；真实 Agent/模型/账户、URL 打开须显式启用，不进入 all，不执行真实 logout。

P1 起结构化报告包含场景/阶段、PASS/FAIL/SKIPPED/NOT_TESTED、耗时、协议/能力、对端版本与脱敏错误；负载报告另附原始参数/指标。N/A 说明可选能力不适用，不能替代本方声明能力的验收。transcript 显式启用并脱敏；取消须区分已发送和远端确认。

`--report <json-path>` 为每个场景输出 phase/status/elapsedMs/protocolVersion；schema 另有覆盖数字、固定对端 SHA、模型能力边界与拒绝输入数。失败记录异常类型，stderr 不输出原始 payload 或路径；报告列明 RPC、真实 Agent、互操作、AOT、Docker 未测。可用 `run schema --fixtures <json-path>` 注入本地失败输入；错误版本/不全覆盖为场景失败（退出 1），非法选项仍为退出 2。控制台禁用 STJ 反射默认解析，模型通过 SDK context 运行；这不等于实际 AOT 验证。真实进程测试检查 schema 成功及错误夹具报告，并在超时后回收自有进程和临时文件。
