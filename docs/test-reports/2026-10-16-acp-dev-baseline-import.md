# ACP dev 基线接收与验证记录

日期：2026-10-16。任务：OPEN-6。本次负责基线导入、集成自审和调度；P0 独立验收由 OPEN-7 完成。本报告不把已有测试通过视为完整 ACP SDK 或 P0 验收放行。

## 固定输入与交接

- 用户指定所有阶段变更基于 `dev`，PR base 为 `dev`。
- dev 输入：`30fde6d5010fc2e57208a95912d7fa7600eaa3f9`；验证内容提交：`2d7bc9afd1104bc96710abefbf6ea84fff0be4fa`。
- 任务分支：`agent/agent/07f480e5d136`；草稿 PR：[OPEN-6 / #231](https://github.com/philfanzhou/OpenAgent/pull/231)。未合并、未发布、未部署。
- 附件：`acp-single-package-baseline-20261016.zip`，SHA256 `7fe0ca9f8b34adf865900ef2f64797aab84cd474377eb88cad94b52ce423cc50`。
- 附件基线：`990f14ca5b563fe81a9a81a5bc4aa053a9d28159`；patch SHA256 `8fc0a8af75b548d33eef64c544c6dde0c4f6bcf4a90f22f2be60d0647adaa34d`。
- 使用 manifest 验证 58 个文件、8 个 tracked 文件的 base 哈希；在干净隔离分支先 `git apply --check` 再应用 patch，复制 new-file 前比较已存在内容，导入后逐项哈希匹配。
- 后续将本任务尚未推送的导入提交 rebase 到 dev，人工解决 5 个共享文件冲突。保留 dev 的模块结构、依赖白名单、集中配置与 .NET 10；将 SDK 规格中的旧 net8.0 对齐为 net10.0。所有 `.cs` / `.csproj` 与附件逐字节一致；共享文档、solution 和 TODO 的适配与原文件哈希差异可审查。
- 原源工作区未访问、未修改。旧 Demo 只保留附件中的回归资料，不作为 SDK 依赖。

## 环境、命令与结果

本机 macOS 26.5 / osx-arm64；.NET SDK 10.0.301、MSBuild 18.6.4，按 dev 的 global.json 选择 SDK；默认离线确定性场景，不启用模型、账户、真实 Runner 或 Bubblewrap 测试。

| 命令 | 结果及意义 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | exit 0；0 错误，70 个依赖审计警告。三个 ACP 工程均随主解决方案构建 |
| `dotnet test Backend/OpenAgent.sln --no-build --logger trx --results-directory ../evidence/dev-test-results` | exit 0；938 通过、0 失败、24 环境门控跳过，共 962 项；包括 ACP 19 项和真实控制台进程测试 |
| `dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- list` | exit 0，列出 framing |
| 同一控制台 `run framing`、`run all` | 分别 exit 0，输出 PASS framing；未调用 Agent/模型 |
| 同一控制台 `run missing` | exit 2，明确拒绝未知场景 |
| `dotnet pack Backend/src/OpenAgent.Runtime.Acp/OpenAgent.Runtime.Acp.csproj --configuration Release --output ../evidence/packages` | exit 0；仅一个 nupkg、一个 `lib/net10.0/OpenAgent.Runtime.Acp.dll`，nuspec 无额外包依赖 |
| 对 tests 和 TestConsole 分别运行 `dotnet pack ... --configuration Release --output ../evidence/nonpackable` | 均 exit 0，不产出 nupkg，符合 IsPackable=false |
| `git diff --check`、`git merge-base --is-ancestor origin/dev HEAD` | 均 exit 0；无空白错误，任务提交包含 dev 基线 |

| 项目 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| OpenAgent.Framework.Tests.dll | 32 | 0 | 0 |
| OpenAgent.Architecture.Tests.dll | 12 | 0 | 0 |
| OpenAgent.Runner.Tests.dll | 36 | 0 | 22 |
| OpenAgent.Contracts.Tests.dll | 58 | 0 | 0 |
| OpenAgent.Application.Tests.dll | 286 | 0 | 2 |
| OpenAgent.Router.Tests.dll | 124 | 0 | 0 |
| OpenAgent.Engine.Tests.dll | 218 | 0 | 0 |
| OpenAgent.Runtime.Acp.Tests.dll | 19 | 0 | 0 |
| OpenAgent.Capabilities.Tests.dll | 153 | 0 | 0 |

24 项跳过为 Application 的 2 项真实 Runner 测试，以及 Runner 的 22 项 Linux/Bubblewrap/服务测试；macOS 默认环境不具备对应门控条件，不能计作平台沙箱已验收。数字来自本次原始 TRX 和退出码，不引用附件历史结果。

## 自审发现与交接约束

1. **P2：CRLF 在消息字节上限处与 LF 不一致。** `Backend/src/OpenAgent.Runtime.Acp/Transport/AcpStreamTransport.cs:72` 在移除 CR 前检查长度。公共 API 探针设置 `MaxMessageBytes=2`，`{}\n` 读取成功，而 `{}\r\n` 抛 InvalidDataException；两者消息体都只有 2 字节。影响：采用 CRLF 的合法上限消息会被拒绝并使连接故障。建议在明确分隔符计数语义后修复，并覆盖 CR/LF 分片、恰好上限与超过上限。证据为 `crlf-probe.log`；探针源码随原始证据交付。OPEN-7 独立复核此项，修复与回归完成后才放行 OPEN-8。
2. 70 个构建警告为继承依赖的 NuGet 审计告警，包含 ImageSharp 高/中严重度告警；本次不扩大为平台依赖升级任务。SDK 无该依赖。质量审查应明确其对发布的影响，不能将构建通过写成依赖安全检查通过。
3. pack 提示缺少包 README；许可证/NOTICE、SourceLink、XML/API 差异及发布元数据按 P7 收口，本次开发包不具有稳定发布资格。

仅创建一个 SDK 包；既有平台依赖白名单未扩大，控制台只调用公共 API。导入时的冲突处理保留 dev 平台结构，没有恢复 Core/Infrastructure/独立 Hosting 项目。

OPEN-8 至 OPEN-14 已停放 backlog，保留原负责人和 stage；只启动 OPEN-7 进行独立验收，不重复实现传输。后续阶段必须携带经核对的分支、SHA、PR、SDK/测试/控制台/文档与独立审查证据，由技术负责人逐项显式放行。

## Preview、未覆盖范围和原始结果

本次为独立纯 SDK，仓库现有 Preview/Docker 流程面向平台宿主，没有 ACP SDK 服务镜像或健康端点；未启动无关 Docker，也未声称完成 Preview。采用实际 .NET 构建、子进程控制台及本地包检查作为适用验证；这不是 Preview/Docker 验证。

未测试完整 RPC、角色/会话/进程 SDK、跨语言、压力、AOT、Windows/Linux、真实模型和真实账户。退出码 1 的场景失败路径尚未故障注入验证。P0 只有 framing；后续能力按阶段交付。

原始 `dev-build.log`、`dev-test.log`、9 份 TRX、环境、控制台、pack/包内容和 CRLF 探针记录通过 OPEN-6 评论附件交付；本机路径不是交付链接。远端 CI 已由 push/PR 触发，本轮不等待或声明其结果。
