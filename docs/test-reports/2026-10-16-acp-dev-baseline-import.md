# ACP P0 接收与验证

任务 OPEN-6，[PR #231](https://github.com/philfanzhou/OpenAgent/pull/231)，base=`dev` `30fde6d5010fc2e57208a95912d7fa7600eaa3f9`。原源工作区未访问。附件 SHA256 `7fe0ca9f8b34adf865900ef2f64797aab84cd474377eb88cad94b52ce423cc50`、patch 及 58 文件均已核对，先 git apply --check 再导入；保留 dev 的模块结构与 .NET 10 配置。

## 已执行验证

环境：macOS 26.5 / osx-arm64，.NET SDK 10.0.301。验证内容 SHA `2d7bc9afd1104bc96710abefbf6ea84fff0be4fa`，结果和原始 TRX 已随 OPEN-6 评论附件交付：

| 实际命令 | 结果 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | exit 0，0 错误、70 个既有依赖审计警告 |
| `dotnet test Backend/OpenAgent.sln --no-build --logger trx --results-directory ../evidence/dev-test-results` | exit 0；938 通过、0 失败、24 门控跳过，含 ACP 19 项 |
| `dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- list`、`run framing`、`run all` | 各 exit 0；仅 framing |
| 同一控制台 `run missing` | exit 2，未知场景明确失败 |
| `dotnet pack Backend/src/OpenAgent.Runtime.Acp/OpenAgent.Runtime.Acp.csproj --configuration Release --output ../evidence/packages` | 仅一个包、一个 net10.0 SDK DLL，无平台依赖 |
| 对测试/控制台分别运行 `dotnet pack <project> --configuration Release --output ../evidence/nonpackable` | 均不产包 |

24 项跳过为 Application 的 2 项真实 Runner 及 Runner 的 22 项 Linux/Bubblewrap/服务测试，不计为通过。依赖审计警告包含平台 ImageSharp 高/中严重度告警，SDK 不依赖该包；构建通过不表示安全告警已解决。包缺 README，发布元数据按 P7 收口。

## 未解决发现与范围

P2：`Backend/src/OpenAgent.Runtime.Acp/Transport/AcpStreamTransport.cs:72` 在移除 CR 前计数。MaxMessageBytes=2 时，`{} + LF` 成功、`{} + CRLF` 抛 InvalidDataException 并使 transport 故障。建议修复分隔符计数并覆盖恰好上限、超限、CR/LF 分片。公共 API 探针及源码已交任务附件；由 OPEN-7 独立复核，修复/回归前不得放行 ACP-01。

仅验证 P0 framing 与实际控制台进程；完整 RPC/角色/会话/进程 SDK、互操作、压力、AOT、Windows/Linux、真实模型/账户及场景失败退出码 1 均未验收。纯 SDK 无适用服务 Preview/健康端点，未做 Docker 验证。未合并、发布或部署；独立审查与阶段状态只见 TODO/Multica。

## PR 清理

按用户要求，移除试验实现、历史调研和旧测试报告；正式文档按职责压缩，sources.json 仅保留 v1 协议及互操作输入，schema/meta 哈希、25 个方法和 14 类更新不变。保留正式 SDK、其自动化测试和调用公共 API 的测试控制台。

本次清理重新执行 `dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests/OpenAgent.Runtime.Acp.Tests.csproj --no-build --logger trx --results-directory ../evidence/cleanup/test-results`：19 通过、0 失败、0 跳过，包含真实控制台进程。另通过 git diff --check、50 个本地文档链接、主解项目路径、删除引用及协议基线一致性检查。正式 SDK/测试/控制台与 solution 内容未改，未重复全量 build/test；上表 938 是指定旧 SHA 的已执行结果，不是本次新跑的全量结果。清理范围不涉及 Preview/Docker。
