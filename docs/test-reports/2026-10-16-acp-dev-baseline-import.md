# ACP P0 集成验证

OPEN-6，[PR #231](https://github.com/philfanzhou/OpenAgent/pull/231)，base=`dev` `30fde6d5010fc2e57208a95912d7fa7600eaa3f9`。本次验证内容 SHA：`58c1713f159362dd744c72c0ab098cc5de4cd74f`；环境为 macOS 26.5 / osx-arm64、.NET SDK 10.0.301。原源工作区未访问；试验代码与历史调研已移除，保留正式 SDK、测试、测试控制台及精简规格。

## 独立审查处置

OPEN-7 对旧 SHA `0d0017f` 的报告确认 F1/F2，并提出 F3/F4 契约风险。原始报告/日志保留在任务附件；本轮同步修改 SDK、回归、控制台及 SPEC：

| 发现 | 当前实现与回归 |
|---|---|
| F1：CR 被计入消息上限 | 上限仅计消息体；跨 read 最多保留一个候选 CR。覆盖 LF/CRLF、UTF-8、1/2/3/4096 字节读取、恰好上限、真超限、CR 后非 LF/EOF |
| F2：取消回调异常跳过清理 | 即使 CancelAsync 抛错仍分别尝试清理输入/输出；保留全部清理异常并结束 Completion，重复 Dispose 重现同一结果 |
| F3：EOF 过早报告成功 | EOF 拒绝新操作，排空已开始的帧；Completion 等在途操作及清理结束并保留晚到 IO 错误。Dispose/故障可中断排空，不提供继续写入的半关闭 API |
| F4：并发 Dispose 提前返回 | 所有调用共享一次清理任务，等待同一结果/异常；覆盖慢清理、两个流均失败、同流只释放一次与 LeaveOpen |

生命周期任务由 transport 持有；自定义 Stream 仍须在取消或释放时使 IO 收敛。Completion 包含 IO/清理故障，Dispose 仅重抛清理故障，不重复抛已报告的 IO 错误。契约见 SPEC 与 IAcpTransport XML。

## 本次实际结果

| 命令/范围 | 结果与解读 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | exit 0；0 错误、70 个既有 NU1902/NU1903 审计警告 |
| `dotnet test Backend/OpenAgent.sln --no-build --logger trx --results-directory ../evidence/p0-fixes/solution-results` | exit 0；974 通过、0 失败、24 门控跳过，ACP 55 项通过（49 传输/生命周期、1 依赖、5 真实控制台进程） |
| `dotnet <TestConsole.dll> list`、`run framing`、`run lifecycle`、`run all` | 各 exit 0；framing 验证字节上限，lifecycle 验证 EOF/取消异常/并发释放，all 包含两者 |
| 同一控制台 `run missing`；隔离输出副本缺失 SDK 后 `run framing` | 分别 exit 2、1；后者为显式故障注入，非正常构建失败 |
| `dotnet run --project ../evidence/p0-fixes/review-probe/probe.csproj` | exit 0，质量探针 27 通过。仅 F2 的重复 Dispose 观察改为捕获并断言同一清理异常，以符合新公开契约；其余探针不变，附源码与差异哈希 |
| `dotnet pack Backend/src/OpenAgent.Runtime.Acp/OpenAgent.Runtime.Acp.csproj --configuration Release --output ../evidence/p0-fixes/packages` | exit 0；一个 nupkg、一个 net10.0 SDK DLL，无平台或测试/控制台依赖 |

新增回归在旧实现上完整运行 35 项：18 通过、17 失败，覆盖上述缺陷/旧契约；修复后全部通过。编写回归期间一次等待顺序问题导致测试中止，修正测试释放顺序后重新获得完整结果；中止运行不计入通过数。原始红/绿、主解 TRX、探针、控制台与 pack 结果随 OPEN-6 附件交付，未以历史数字代替本次结果。

主解通过数：Framework 32、Architecture 12、Contracts 58、Runner 36、Application 286、Router 124、Engine 218、Capabilities 153、ACP 55。跳过为 Application 的 2 项真实 Runner 和 Runner 的 22 项 Linux/Bubblewrap/服务测试，本机未启用对应环境门控，不能算作沙箱验证。

## 独立复审证据核对

[OPEN-7 复审报告与原始附件](https://multica.ai/openagent/issues/01a142a2-9ff3-7113-83a3-21f1d599903b) 固定于 `3be97c118f87cd714012bc5c0889dcfe2ad7b4c0`，环境同上。质量安全工程师独立重跑主解 build/test、控制台、SDK/测试/控制台 pack 及公共 API 探针：974 通过、0 失败、24 跳过，ACP 55；原探针 27/27，新增边界/生命周期探针 123/123；退出码与单包内容符合上表。新增探针首次有一处过窄的排队取消断言，按故障可取消排空的公开契约修正后完整重跑，初始源码和失败日志保留在附件。

技术负责人本次使用 Python hashlib/ElementTree 核对证据 ZIP 中 70 个文件哈希、9 份 TRX 的实际结果、38 个变更文件与当前仓库的哈希，并阅读探针差异、F1–F4 断言、日志及最终 PR 范围，全部一致。F1/F2 修复和 F3/F4 契约通过技术验收；精简正式规格无新增阻断。本轮仅更改报告/TODO，`git diff --exit-code 3be97c1 HEAD -- Backend` 验证产品内容不变，未重复运行已通过的构建/测试。人工验收和阶段状态见 TODO；此结论不表示完整 SDK 或发布就绪。

## 剩余范围

平台 ImageSharp 既有高/中严重度告警未在本次升级；SDK 不依赖该包，构建成功不表示安全告警已解决。包 README/许可/SourceLink/XML/API 发布收口仍按 P7 验收。本次只有 P0，未测完整 RPC/角色/会话/进程 SDK、互操作、压力、AOT、Windows/Linux 或真实模型/账户。纯 SDK 无服务 Preview/健康端点，未运行 Docker；未合并、发包或部署。
