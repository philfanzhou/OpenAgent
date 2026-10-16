# OPEN-8 / ACP-01：模型与可重复生成验证

被测实现提交：`71bf602a5ab04bf962b8d6dac456b8587bab8526`，分支 `agent/agent/6ddf5f22ccda`，[PR #232](https://github.com/philfanzhou/OpenAgent/pull/232)，base=`dev`。固定交接 `4239209ca5180329df60574be67d390e7897d5bd` 为祖先；包含未合并的 P0（PR #231）。后续仅补本报告和 TODO；最终交付 SHA 见 PR/任务评论，附件 delivery.json 同时记录两者和源文件哈希。

## 范围与环境

macOS 26.5 / arm64（osx-arm64），.NET SDK 10.0.301、运行时 10.0.9。默认离线确定性用例；首次下载协议原件后，生成/测试不联网获取 schema，不调用模型、账户、URL 或个人仓库。

唯一 SDK/包仍为 `OpenAgent.Runtime.Acp`；新增非发布 CodeGen，并一次登记主解。没有新增集中包依赖、修改 TFM/平台边界或执行链。已验收的 Transport 与其回归测试相对交接提交无差异。测试对 CodeGen 的 ReferenceOutputAssembly=false 引用只保证生成器参与测试构建，不进入 SDK/消费者依赖。

## 固定来源

原件取自 `agentclientprotocol/agent-client-protocol` 的 commit `1cebe1c01837379abf34dd62b71e9c75bf140669`，完整字节保存在 `docs/integrations/acp-dotnet-sdk/schema/v1/`。来源见 [固定上游](https://github.com/agentclientprotocol/agent-client-protocol/tree/1cebe1c01837379abf34dd62b71e9c75bf140669)，对应 sources.json 与 schema/lock.json；未使用 latest/v2。

| 原件 | SHA256 |
|---|---|
| schema.json | `4bcd1d2051ff7ed379bd9b234fb9b09c5ab9245478698e9fab805bd9b52a33a6` |
| meta.json | `061edb6efa8fb2aa2792459a86ec7268de5fe665bba48b2ffe7939df01481f88` |
| Apache-2.0 LICENSE | `f250d08cee4549b22b3b4aaaf3a743473336fd280316df5d0340717e5127a221` |

生成器核对哈希/lock、协议版本、178 定义、meta/x-method/x-side 和全部 14 更新。原件禁用 Git 文本转换，生成 C# 固定 UTF-8/LF；SDK 内嵌固定 schema，运行时无需原件目录或生成工具。

## 实测命令与结果

以下从仓库根目录执行；原始命令、退出码、耗时在附件 verification-commands.json，主解 TRX 在 final-trx/。

| 实际命令 | 结果与解读 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | 退出 0；0 错误，70 个既有 ImageSharp 审计警告，未解决或压制 |
| `dotnet test Backend/OpenAgent.sln --no-build --logger trx --results-directory artifacts/open-8/final-trx` | 退出 0；1416 通过、0 失败、24 门控跳过（总计 1440，9 个测试程序集） |
| `dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests --logger 'trx;LogFileName=p1-final.trx' --results-directory artifacts/open-8` | 退出 0；497 通过、0 失败、0 跳过；主解亦包含该组 |
| `dotnet run --configuration Release --project Backend/tools/OpenAgent.Runtime.Acp.CodeGen -- .` | 退出 0；生成 608 文件，生成与手写转换/校验分离 |
| `python3 Backend/tools/OpenAgent.Runtime.Acp.CodeGen/verify.py . artifacts/open-8/codegen-verification.json Release` | 退出 0；生成前、连续两次写出后所有文件名/字节 SHA256 一致，新增/删除/变化均为 0 |
| `dotnet pack Backend/src/OpenAgent.Runtime.Acp --configuration Release --output artifacts/open-8/pack` | 退出 0；一个 SDK DLL、0 包依赖，附 licenses/LICENSE 与 licenses/NOTICE；README 等 P7 元数据仍未收口 |

CodeGen 已先行用 Release 构建；verify.py 的 5 个隔离篡改探针分别修改 schema、meta、LICENSE、lock 和方法清单，5/5 非零且未创建生成输出。既有 CI 通过新增的 CodeGenProcessTests 两次运行 `--check`，在内存中重新生成并比对已提交文件，无需修改工作流文件。初次推送因 workflow scope 被 GitHub 拒绝后，已改为此测试门禁；未变更任何凭据/权限。

24 项跳过为 Runner 的 22 项 Linux/Bubblewrap/真实 HTTP 门控测试，以及 Application 的 2 项真实 Runner 集成；原始名称见 test-summary.json。TRX Counters.notExecuted 在此适配器为 0，按 Results 的 NotExecuted 与 total-executed 交叉核对得到 24，不能当作沙箱或集成通过。

## 覆盖与公共 API

固定 contracts.json 有 307 个输入：178 个定义、全部稳定方法参数/结果（21 request、4 notification，共 46 schema 类型）、14 更新及各顶层联合分支/枚举值。每例核对兼容读取、严格往返、全部公共 getter，以及可用的强类型联合投影；另对每个方法测试非法根类型、适用的必填属性缺失。针对性用例覆盖：

- 必填/null/大小写/重复键、闭合与开放枚举、{} 能力与未公布/null 区别；已知 tag 加坏 payload 不回退为其他分支。
- schema 标记的 default-on-error/skip-invalid-items、显式默认 getter；Optional 保留 omitted/null/value 与空数组，不把局部更新缺省成空集合。
- RpcId string/int64/null、超过 2^53 的精确值及 int64/uint64 边界；拒绝小数/指数 ID、溢出和非法类型。
- 输入 JsonDocument 释放后 `_meta`、开放 JSON、错误 data 仍可读；返回集合无法修改内部值。拒绝、取消 stop reason 和权限 outcome 仅建模，不执行业务行为。
- P0 原有 framing/lifecycle、CRLF 精确上限、EOF 排空/Completion、取消回调异常清理、共享 Dispose 与 SDK 依赖门禁回归。

新增公开入口：AcpProtocol、AcpJson、AcpJsonContext、AcpSchemaDescriptor/AcpMethodDescriptor、AcpModel 与生成 DTO/联合分支、wire 常量、Optional<T>、RpcId、Error 与 AcpRpcException。完整编译后导出快照见 public-api.txt（包括已存在 Transport）。协议标量别名映射 CLR 基础类型；目录可单独校验别名的 schema 约束。

兼容性决策：不可变模型持有 owned JsonElement；强类型构造/发送严格校验，读取仅按 schema 明确标记恢复可选字段。可选属性三态与 `*OrDefault` 分开，默认不注入 wire。标准对象未知根字段读取后丢弃；schema 明确开放的 JSON/未来 elicitation 分支保留 raw，不作为已知成功动作。URI/日期 format 只作注解，绝不规范化或访问资源。源生成 context 使用具体 converter，不依赖反射扫描。

## 控制台、包与自审

真实 `dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- ...` 实测：list、run framing、run lifecycle、run schema --report、run all --report 均退出 0；未知场景退出 2；错误版本夹具退出 1 并生成 FAIL 报告。7/7 退出码符合预期，stdout/stderr 及完整参数在 console-commands.json；schema/all 报告覆盖 178/25/14 和 307 夹具，非法读取/发送拒绝 48 项。控制台禁用 STJ 默认反射，且只引用 SDK 公共 API。8 个控制台进程用例（含成功/失败 JSON 报告）有超时、回收自有进程及临时文件清理。

nupkg 实查仅 `lib/net10.0/OpenAgent.Runtime.Acp.dll`；无测试、控制台、CodeGen、平台程序集或 NuGet 依赖。package-inspection.json 记录包内容、SHA256 与 repository commit；这不是发布验收或 NuGet 发布。

自审检查手写实现、生成器/生成目录一致性、锁文件、主解/测试引用、公开 API 及相对 dev 的 PR 范围。补测发现 grouped 配置选项数组缺少 STJ 注册，已修复并通过全部 getter/投影回归；包内许可目录经实查修正。没有剩余已知 P1 阻断；新增公共模型、solution 登记与测试门禁仍待技术负责人和独立质量审查。

## 未覆盖与风险

本阶段只交付模型，25 方法不可据此宣称可调用；未实现 P2 RPC 引擎、能力门控、会话/handler 或平台执行链。无服务 Preview/镜像/健康端点，因此 Preview/Docker 不适用，未运行 Docker；本机 pack/控制台不冒充 Preview、跨平台、AOT 或真实互操作。未验证真实 Agent/模型、账号、压力/慢消费及 Windows/Linux。P7 包 README/许可元数据/SourceLink/XML/API 发布收口仍待完成；70 个既有 ImageSharp 审计警告未解决。未合并、发布或部署，OPEN-9 至 OPEN-14 不启动。

原始证据由 OPEN-8 最终评论附件交付：环境、命令/日志、TRX、逐文件生成前后 SHA256、篡改结果、控制台成功/失败 JSON、包检查、公开 API、交付哈希和自审清单。运行时本地路径不作为可访问交付链接。
