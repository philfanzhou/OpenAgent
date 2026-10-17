# OPEN-8 / ACP-01：模型与可重复生成验证

被测实现提交：首轮 `71bf602a5ab04bf962b8d6dac456b8587bab8526`（独立复核 OPEN-15 结论「不通过，1 项阻断」）；本报告对应修复提交 `707d371634328a32cac53ae9037378d12fb12ef7`，分支 `agent/agent/6ddf5f22ccda`，[PR #232](https://github.com/philfanzhou/OpenAgent/pull/232)，base=`dev`。固定交接 `4239209ca5180329df60574be67d390e7897d5bd` 为祖先；包含未合并的 P0（PR #231）。后续仅补本报告和 TODO；最终交付 SHA 见 PR/任务评论，附件 delivery.json 同时记录两者和源文件哈希。

首轮交付后的独立复核指出 F-A（信封联合「首个匹配」静默丢弃 `params`/`result` 载荷，阻断）、F-B（`AuthMethod` 未知 `type` 降级为 agent）、F-C（运行时 323 定义与 178 目录差异未交代）、F-D（`result`+`error` 同现被吞）、F-E（负数覆盖与注释不实）、F-F（四个公共 API 陷阱）。本修复轮逐项落地并补齐回归门，见下文；首轮报告中的覆盖声明就此修正：**首轮 307 夹具不含任何信封载荷，兼容读取/严格往返/getter 断言未覆盖信封联合，首轮恒绿正是这一盲区所致。**

## 范围与环境

macOS 26.5 / arm64（osx-arm64），.NET SDK 10.0.301、运行时 10.0.9。默认离线确定性用例；生成/测试不联网获取 schema，不调用模型、账户、URL 或个人仓库。

唯一 SDK/包仍为 `OpenAgent.Runtime.Acp`；非发布 CodeGen 与主解登记维持首轮状态。没有新增集中包依赖、修改 TFM/平台边界或执行链。已验收的 Transport/`Recovery` 契约相对交接提交无差异；本轮改动仅限 Protocol 校验、生成器、契约测试、控制台 schema 场景与文档。

## 固定来源

原件取自 `agentclientprotocol/agent-client-protocol` 的 commit `1cebe1c01837379abf34dd62b71e9c75bf140669`，完整字节保存在 `docs/integrations/acp-dotnet-sdk/schema/v1/`。来源见 [固定上游](https://github.com/agentclientprotocol/agent-client-protocol/tree/1cebe1c01837379abf34dd62b71e9c75bf140669)，对应 sources.json 与 schema/lock.json；未使用 latest/v2。

| 原件 | SHA256 |
|---|---|
| schema.json | `4bcd1d2051ff7ed379bd9b234fb9b09c5ab9245478698e9fab805bd9b52a33a6` |
| meta.json | `061edb6efa8fb2aa2792459a86ec7268de5fe665bba48b2ffe7939df01481f88` |
| Apache-2.0 LICENSE | `f250d08cee4549b22b3b4aaaf3a743473336fd280316df5d0340717e5127a221` |

固定原件本轮无任何字节变化；运行时嵌入 schema 与生成目录由生成器确定性重建。生成器除既定转换外新增一条有据可查的规则：`AuthMethod` 的 agent 候选按 PROTOCOL §3.4/§5 钉上 `type:"agent"` 常量（F-B），嵌入 schema 随之自描述，定义总数仍为 178 固定 + 145 合成 = 323。

## 实测命令与结果

以下从仓库根目录执行；原始命令、退出码、耗时在附件 verification-commands.json，主解 TRX 在 final-trx/。

| 实际命令 | 结果与解读 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | 退出 0；0 错误，70 个既有 ImageSharp 审计警告，未解决或压制 |
| `dotnet test Backend/OpenAgent.sln --no-build --logger trx --results-directory artifacts/open-8/final-trx` | 退出 0；1522 通过、0 失败、24 门控跳过（总计 1546，9 个测试程序集） |
| `dotnet test Backend/tests/OpenAgent.Runtime.Acp.Tests --logger 'trx;LogFileName=p1-fix.trx' --results-directory artifacts/open-8` | 退出 0；603 通过、0 失败、0 跳过（首轮 497，新增 106 个信封/契约/公共 API 用例）；主解亦包含该组 |
| `dotnet run --configuration Release --project Backend/tools/OpenAgent.Runtime.Acp.CodeGen -- .` | 退出 0；生成 608 文件，生成与手写转换/校验分离 |
| `dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.CodeGen -- . --check`（连续两次） | 退出 0×2；`Verified 608 generated files without writing` |
| `python3 Backend/tools/OpenAgent.Runtime.Acp.CodeGen/verify.py . artifacts/open-8/codegen-verification.json Release` | 退出 0；生成前、连续两次写出后所有文件名/字节 SHA256 一致，新增/删除/变化均为 0 |
| `dotnet pack Backend/src/OpenAgent.Runtime.Acp --configuration Release` | 退出 0；包内仅一个 SDK DLL、0 包依赖，附 licenses/LICENSE 与 licenses/NOTICE；README 等 P7 元数据仍未收口 |

CodeGen 已先行用 Release 构建；verify.py 的 5 个隔离篡改探针分别修改 schema、meta、LICENSE、lock 和方法清单，5/5 非零且未创建生成输出。既有 CI 门禁为 CodeGenProcessTests 两次运行 `--check`，在内存中重新生成并比对已提交文件。24 项跳过为 Runner 的 22 项 Linux/Bubblewrap/真实 HTTP 门控测试，以及 Application 的 2 项真实 Runner 集成；不能当作沙箱或集成通过。

## F-A/F-D：信封判别与互斥（本轮核心修复）

根因是生成器把上游内联 `anyOf` 提升为合成定义后，校验器对信封联合取「首个校验通过的候选」并输出其规范化结果，`required` 为空的候选（`LogoutRequest`、`WriteTextFileResponse` 等）成为吞掉后续候选的兜底分支。修复后：

- `ClientRequest`/`AgentRequest`/`ClientNotification`/`AgentNotification` 的 `params` 按**带内 `method`** 从 `x-method` 联合中选唯一候选并校验；校验失败即拒绝，不做跨候选回退。已知方法但方向不符显式拒绝；目录外未知方法交给 ext 变体原样保留（上游 `ExtRequest`/`ExtResponse`/`ExtNotification` 语义）。
- `ClientResponse`/`AgentResponse` 无带内方法：`result` 只做「必须为 JSON 对象」的结构校验并**逐字保留**，不再伪装成某个候选的规范化输出；类型化校验由叶子模型构造、`As*()` 投影（严格）或目录描述符承担，P2 方法级引擎接入后按请求方法选择。`result` 与 `error` 同时出现一律 `JsonException`（F-D）。
- 合成联合单独反序列化定义为原样视图（保留全部字段、拒绝重复键与非对象），文档写明其边界。

## 覆盖与公共 API

固定 contracts.json 现有 **352** 个输入（`fixtures/build.py` 从锁定 schema 确定性重建，幂等）：178 个定义、全部方法参数/结果/通知载荷、14 更新、顶层联合分支/枚举值，以及 **45 个方法方向信封夹具**（21 request + 21 response + 3 notification；`$/cancel_request` 为协议级通知，上游无 envelope 联合，按定义级覆盖并在测试中断言其缺席）。每例附 `required` 元数据（来自锁定 schema）；每例核对兼容读取、严格往返、全部公共 getter 及可用的强类型联合投影。

信封回归门（`EnvelopeContractTests`）：

1. 45 个信封夹具逐一断言类型化读取与写出的深比较与输入一致，0 处静默丢失。
2. 每个方法 payload 的方向信封存在且信封类型与方向一致；`$/cancel_request` 显式缺席。
3. `session/prompt` 信封投影为 `PromptRequest`（`sessionId` 可读），`AsLogoutRequest()` 抛 `JsonException`；合成联合单独读取原样保留后同样判别。
4. 定向负例：已知方法但载荷畸形/方向不符、未知方法 ext 保留、`result`+`error` 互斥、非对象 `result` 拒绝、`AuthMethod` 未知/不匹配 tag 拒绝（F-B，`"agent"` 显式值与缺失值均归 agent 变体且类型字段保留）。

首轮报告的 `Deserialize_RequiredMethodProperties_CannotBeRecovered` 以夹具自身键推导「必需集」且 13/46 空 `required` 用例空转，与「Independent input knowledge」注释不符（F-E）。现已改为夹具内置锁定 `required` 元数据：非空集合逐键删除必失，空集合显式标注无可断言（联合分支级判别由 `Union_KnownMalformedPayload_DoesNotFallBack` 覆盖）。夹具中还把 `definition-AgentResponse`/`variant-AgentResponse-1` 的 `protocolVersion:0` 样例修正为合法的协商结果 1。

针对性模型用例保留首轮全部范围（必填/null/大小写/重复键、闭合与开放枚举、{} 能力、default-on-error/skip-invalid-items、RpcId int64 边界、owned JSON 生命周期），并新增 F-F 修复：`Optional<JsonElement>` 的 Undefined 元素按缺失处理不再在序列化时抛 `InvalidOperationException`；抽象 `AcpModel` 的反射序列化与 `AcpJson` 一致抛 `NotSupportedException`；`AcpSchemaDescriptor.JsonTypeInfo` 注明为无校验的底层出口（`ToolKind` 等标量别名在锁定 v1 schema 即为自由字符串，非 SDK 放宽）；显式 null 落在非 nullable 可选标记字段按缺失归一的例外写入 `Optional` 文档与 PROTOCOL。

新增公开入口与首轮一致：AcpProtocol、AcpJson、AcpJsonContext、AcpSchemaDescriptor/AcpMethodDescriptor、AcpModel 与生成 DTO/联合分支、wire 常量、Optional<T>、RpcId、Error 与 AcpRpcException。本轮未新增公共类型；完整编译后导出快照见 public-api.txt。

## 控制台、包与自审

真实 `dotnet run --no-build --project Backend/tools/OpenAgent.Runtime.Acp.TestConsole -- ...` 实测：list、run framing、run lifecycle、run schema --report、run all --report 均退出 0；stdout 为 `PASS schema … definitions=178 envelopes=45 (model coverage)`。schema 报告机器可读覆盖现含信封载荷：`fixtureCount=352`、按信封类型的 `envelopes` 计数（12/9/9/12/1/2），并按构成列出 `rejectedInputs`：`rootTypeRejections=46`（46 个方法 schema 的 `[]` 根类型负例）、`envelopeRejections=3`（方向不符、方法内畸形、result+error 互斥）、`targetedRejections=2`（首轮的 48 实为 46 次同类根类型 + 2 定向，表述已修正，F-E）。8 个控制台进程用例（含成功/失败 JSON 报告）有超时、回收自有进程及临时文件清理。

nupkg 实查仅 `lib/net10.0/OpenAgent.Runtime.Acp.dll`；无测试、控制台、CodeGen、平台程序集或 NuGet 依赖。这不是发布验收或 NuGet 发布。

自审核对：修复未触碰 `Transport/`；单包 `IsPackable`/TFM/集中配置不变；生成两次写出字节一致、5/5 篡改探针拒绝、`--check` 无漂移；`git status` 仅含本轮列出的文件。没有剩余已知 P1 阻断；信封读取语义（响应 `result` 的无方法保留边界）已写入 PROTOCOL §6，仍待技术负责人与独立质量审查确认。

## 未覆盖与风险

本阶段只交付模型，25 方法不可据此宣称可调用；未实现 P2 RPC 引擎、能力门控、会话/handler 或平台执行链。响应信封在无请求方法上下文时只做结构校验并保留 `result`，逐方法校验依赖类型化构造/投影或 P2 引擎——这是文档化的语义边界，不是已实现的能力。无服务 Preview/镜像/健康端点，Preview/Docker 不适用，未运行 Docker；本机 pack/控制台不冒充 Preview、跨平台、AOT 或真实互操作。未验证真实 Agent/模型、账号、压力/慢消费及 Windows/Linux。P7 包 README/许可元数据/SourceLink/XML/API 发布收口仍待完成；70 个既有 ImageSharp 审计警告未解决。未合并、发布或部署，OPEN-9 至 OPEN-14 不启动。

原始证据由 OPEN-8 评论附件交付：环境、命令/日志、TRX、逐文件生成前后 SHA256、篡改结果、控制台成功/失败 JSON、包检查、公开 API、交付哈希和自审清单。运行时本地路径不作为可访问交付链接。
