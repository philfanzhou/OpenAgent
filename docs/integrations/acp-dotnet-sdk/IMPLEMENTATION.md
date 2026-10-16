# C# ACP SDK 分阶段实现任务

> v0.3 · 2026-10-16。范围是一个 SDK 包、一个自动化测试工程、一个测试控制台；本文定义任务与验收，不维护完成状态。当前状态、负责人和验收记录统一见 [任务清单](../../planning/TODO.md)。

## 1. 每一步都遵守的交付规则

每个能力按 **SDK 实现 → 自动化测试 → 控制台场景 → 控制台进程测试 → 文档/回归** 推进，在同一个可评审变更中交付。测试先描述 wire 和异常行为，再验证公共 API；不以“类型已生成”“控制台能输出成功”代替协议正确性。

- SDK：新增/修改强类型入口、校验、取消、资源释放和诊断，保持同一个 csproj/包。
- 自动化测试：至少覆盖正常、拒绝/非法、取消或关闭等适用路径；涉及异步时验证 Task 收敛及顺序。
- 控制台：新增可单独运行的确定性场景，`list` 可发现，`run all` 包含已实现的离线场景。
- 控制台自动化：启动真实控制台进程，检查行为、退出码、超时和清理；不能只单测命令解析。
- 证据：记录实际命令、通过/失败/跳过及未覆盖边界；默认无模型、无个人工作目录、无外部服务依赖。

## 2. 阶段与依赖

| 阶段 / 任务 | SDK 交付 | 自动化测试交付 | 控制台交付 | 前置 |
|---|---|---|---|---|
| P0 / ACP-00 | 单包工程、Stream 传输基线 | framing、所有权、SDK 依赖、控制台进程 | list / run framing / run all | 无 |
| P1 / ACP-01 | 固定 schema、生成模型与序列化 | schema/字段/联合分支/ID 契约 | schema 场景、结构化报告 | ACP-00 |
| P2 / ACP-02 | 双向 RPC、分派、配额、关闭与取消 | 重入、响应关联、batch、慢处理与 EOF | rpc / reverse / fault 场景 | ACP-01 |
| P3 / ACP-03 | 最小 Client/Agent、Session/PromptTurn | 握手、新建、流式、取消、早到通知 | agent echo、client、echo / cancel 场景 | ACP-02 |
| P4 / ACP-04 | Processes、stdio、进程租约 | 启动/退出、stderr、关停、共享所有权 | 外部 Agent 启动参数及 process 场景 | ACP-03 |
| P5 / ACP-05 | 完整会话、配置、14 类更新 | 回放/恢复、分页、配置与顺序 | session / config / updates 场景 | ACP-04 |
| P6 / ACP-06 | 权限、文件、终端、elicitation、认证 | 能力门控、拒绝、交互取消和资源清理 | 各项交互场景及可选人工交互 | ACP-05 |
| P7 / ACP-07 | 发布收口、AOT 与兼容策略 | 跨语言、压力、平台、AOT、包检查 | interop / load、版本与报告汇总 | ACP-06 |

阶段号仅是顺序，不是工期承诺。P0 的本地完成不能代替 P1–P7；阶段内按下列子任务逐步合入，未合并状态继续保留在任务清单。

## 3. P0：单包工程与测试基线

- **ACP-00.1 SDK**：创建指定目录下的唯一可发布类库；提供 IAcpTransport、AcpStreamTransport、大小限制与 LeaveOpen。只做字节 framing，不把它称为完整 RPC。
- **ACP-00.2 测试**：验证 UTF-8 分片、CRLF/多帧、半帧 EOF、消息字节上限、并发完整写入、写前/写中取消、释放和禁止平台依赖。
- **ACP-00.3 控制台**：建立 SDK 引用、list/run 命令、framing 场景及 0/1/2 退出码；由自动化测试启动真实控制台。
- **ACP-00.4 集成**：三个工程加入主解决方案，build/test；本地 pack 检查只包含一个 SDK 程序集，测试/控制台不可打包。

验收：三者均可构建，framing 场景通过；故意错误命令返回非零。记录仅传输基线的边界，旧 Demo 不作为 SDK 依赖。

## 4. P1：协议模型与代码生成

- **ACP-01.1** 固定稳定 v1 schema/meta/lock、来源/哈希/许可；建立可重复 CodeGen，分离 Generated 与手写代码。
- **ACP-01.2** 生成 DTO、方法目录、STJ context、错误和 RpcId；复核缺失/null、默认值、{} 能力、判别联合、_meta 内存拥有权、int64。
- **ACP-01.3** 同步契约夹具与 schema 控制台场景：读取固定输入，展示协议版本与覆盖范围；新增机器可读报告格式及其进程测试。

验收：生成重复运行无差异；覆盖全部稳定方法/更新的模型；非法输入与兼容反序列化规则均有测试。仍不宣称方法可实际调用。

## 5. P2：可重入双向 RPC

- **ACP-02.1** 用同一组探针对比 StreamJsonRpc 与自有实现，记录选型；对外不暴露第三方 RPC 类型。
- **ACP-02.2** 实现持续 reader、入站独立 dispatcher、出站 pending、有序 writer、request/notification/batch、标准错误和扩展注册。
- **ACP-02.3** 实现一般请求取消、任务/缓冲配额、重复/迟到响应处理、统一关闭；慢 handler 不阻塞反向调用、响应与 cancel。
- **ACP-02.4** 增加 rpc/reverse/fault 控制台场景及进程测试；自动注入 EOF、超大消息、同号双向请求、慢处理和错误响应。

验收：A→B request 内等待 B→A request 能完成；所有失败/关闭路径在测试期限内收敛；无未观察任务。映射验收 V02/V03/V05/V10/V12/V18。

## 6. P3：最小 Client / Agent 闭环

- **ACP-03.1** 实现角色 API、builder/handler 注册、initialize 和能力门控；未初始化/不兼容版本明确失败。
- **ACP-03.2** 实现 session/new、prompt、update、cancel、文本/资源链接、默认拒绝权限；提供 Session/PromptTurn，验证早到更新。
- **ACP-03.3** 控制台增加 `agent echo` 与 `client` 模式，以及 echo/cancel 场景；Echo 是 SDK handler，stdout 只写协议，诊断走 stderr。
- **ACP-03.4** 自动测试启动两侧进程，验证实际 wire、取消尾部更新、最终结果、会话继续使用及关闭；逐项复用旧 Demo 的行为夹具。

验收：无需模型账户即可完成真实 stdio 双进程的中文/表情流式交互与取消。控制台中不能再包含 Demo 的独立协议循环。

## 7. P4：本地 Agent 进程管理

- **ACP-04.1** SDK 接受 executable + ArgumentList、cwd、环境策略，返回 transport 与 ProcessLease；持续排空 stderr，提供退出信息。
- **ACP-04.2** 实现拥有权、stdin EOF、关停宽限和最终回收；关闭单 session 不误杀共享 Agent。
- **ACP-04.3** 自动化测试覆盖找不到命令、非零退出、大量 stderr、关停中崩溃、慢读写；仅使用临时目录和自己启动的进程。
- **ACP-04.4** 控制台 client 增加外部 Agent 启动参数、超时和 cwd；添加离线 process 场景。真实模型测试须显式启用，不能进入 run all。

验收：进程与请求无泄漏；离线场景通过，外部 Agent 握手与真实模型成功分别记录，不互相替代。

## 8. P5：会话、配置与完整更新

- **ACP-05.1** SDK + 测试 + session 场景：load 回放先于响应、resume 无回放、list 分页、close 与 delete 差异；宿主提供内存示例存储。
- **ACP-05.2** SDK + 测试 + config 场景：modes/configOptions、boolean 能力、设置与更新；长 prompt 期间仍可设置允许的配置。
- **ACP-05.3** SDK + 测试 + updates 场景：14 类更新、完整内容类型、关联与顺序；多 session 隔离、早到通知和慢消费故障。

验收：V06–V08/V12/V16 适用用例通过；每一子功能的控制台报告可独立重现。内存示例不宣称生产持久化。

## 9. P6：按能力逐项完成交互

每一行是独立小步，均执行本页第 1 节的同步交付规则，不能等最后再补测试和控制台。

| 子任务 | SDK 与宿主接口 | 自动化验收重点 | 控制台场景 |
|---|---|---|---|
| ACP-06.1 权限 | 有效 optionId、一次性选择/取消、默认拒绝 | 拒绝、重复回答、取消竞态、跨 session | permission |
| ACP-06.2 文件 | 读写回调与示例授权策略 | 行/limit、真实路径、符号链接、越界、创建文件 | files，临时目录内 |
| ACP-06.3 终端 | create/output/wait/kill/release 与绑定 | UTF-8 截断、kill 保留输出、release 失效、关闭回收 | terminal，自有无害进程 |
| ACP-06.4 Elicitation | form/url 能力、结果和完成通知 | accept/decline/cancel、schema、connection 关联 | elicitation，默认模拟 UI |
| ACP-06.5 认证 | auth 方法、authenticate、logout 显式门控 | 缺省类型、错误、凭据不入日志、取消 | auth，确定性假身份 |

验收：V09/V13–V17/V18 适用用例通过；未注册能力不能对外公布，文件/命令不因安装包自动启用。真实账户 logout 不进入测试。

## 10. P7：互操作与发布收口

- **ACP-07.1** 用固定版本的官方 TS/Python/Rust SDK 双方向互操作；TCK 作辅助，逐项记录 N/A/SKIPPED/NOT_TESTED。
- **ACP-07.2** 验证多 session、高更新量、慢消费与所有关闭路径；控制台增加 load 场景和原始参数/指标报告，不能只给成功字样。
- **ACP-07.3** 实际 AOT publish/run、普通跨平台运行；测试/控制台同步适配裁剪，报告验证过的平台，不由 STJ context 推断全链路通过。
- **ACP-07.4** 核查单一 NuGet 内容、依赖、许可、SourceLink、XML/API 变更和升级文档。真实 Agent 测试显式开启，完整 v1 达标后才发布稳定版。

验收：以 VALIDATION.md 全部发布门槛为准。对外只发布 OpenAgent.Runtime.Acp，一个包内提供全部 SDK 功能；控制台、测试与生成器不进入消费者依赖。
