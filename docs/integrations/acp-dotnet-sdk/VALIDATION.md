# ACP 单包 SDK 测试与验收规格

> 本页是完整 SDK 的建议验收范围，不是当前任务状态或已通过报告。旧基础 C# Demo 的实测结果单列于 [Demo 验证记录](../../test-reports/2026-10-15-acp-demo.md)，不等同下述完整 SDK 矩阵。当前任务状态仍从 `docs/planning/TODO.md` 管理。

## 1. 阶段与同步验收

采用 ACP-00～ACP-07 八个阶段，完整任务和前置关系见 [IMPLEMENTATION.md](./IMPLEMENTATION.md)。验收按 **SDK、自动化测试、测试控制台** 三项同时完成判定，不以单一功能展示替代自动验证。

- 每阶段至少包含对应成功、失败及适用的取消/关闭用例；控制台新增同能力场景并加入其进程测试。
- 合入前运行相关单测、控制台场景与主解决方案 build/test；共享 RPC/生命周期改动须回归之前所有场景。
- 控制台只调用 SDK 公共 API；不得增加第二套协议循环来绕过 SDK 缺失能力。
- 完成证据包括实际测试数、跳过原因、控制台退出码与清理结果。阶段待合并和本地通过分别记录。
- P0 传输基线实测见 [单包基线验证](../../test-reports/2026-10-16-acp-single-package.md)，不代表下列完整 SDK 条目已全部验收。

验收建议以确定性故障注入证明行为，不以精确耗时断言或镜像实现的测试凑覆盖率。协议复杂分支按下表覆盖；真实模型和平台沙箱属于额外边界。

## 2. RPC 库选型探针

候选一：StreamJsonRpc + NewLineDelimitedMessageHandler；候选二：自有小型 NDJSON/RPC runtime。采用同一组 wire fixtures 比较：

- 正确性：原始 ID、双向嵌套调用、取消协议名称、request/notification/batch、错误映射、更新顺序。
- 可维护性：所需适配量、公共 API 泄漏、依赖体积、schema 校验集成。
- 生命周期：挂起 handler 可否被连接关闭取消，读写半关闭和进程退出能否收敛。
- AOT/裁剪：source-generated serializer 与 handler 注册是否存在反射路径，能否实际发布并运行。

若成熟库全部通过且适配更小，优先复用；若必须替换核心行为或 AOT 不满足再选择自有 runtime。选型结果作为后续 ADR，不在本次草案中伪称已经定案。

## 3. 必须覆盖的行为用例

| 编号 | 场景 | 通过条件 |
|---|---|---|
| V01 | 稳定 schema 与生成模型 | 所有方法和分支被覆盖；必填/默认/nullable/int64/unknown 规则与固定 schema 一致 |
| V02 | framing | 半包、多包、长行、跨字符、转义换行、CRLF；stderr 不污染 stdout；非法 JSON 可恢复 |
| V03 | RPC | ID string/int64/null/缺失区分，未知方法，单/混合/空 batch，无响应通知，重复/迟到响应 |
| V04 | 初始化 | 版本不匹配，能力缺省，{} 与 null 差异，未初始化调用，虚假能力配置被拒绝 |
| V05 | 双向调用 | prompt 等待权限期间反向调用可完成，cancel 可到达，无读循环死锁 |
| V06 | 会话 | new 唯一 ID；load 回放在响应前完成；resume 无历史回放；list 分页；close 与 delete 区别 |
| V07 | 早到更新 | load/new 响应之前的通知不丢失、不误绑；未绑定事件可观测且缓冲有界 |
| V08 | 流式更新 | 14 类 update 结构完整，消息/工具/压缩按各自 ID 关联；最终响应不越过更新入队顺序 |
| V09 | 取消 | prompt + pending permission + pending terminal wait；取消后尾部更新；自然完成竞态；仅一份最终响应 |
| V10 | 取消无确认 | Agent 忽略 cancel；宽限后抛未确认异常并关闭连接；不报告假 cancelled，不无限 pending |
| V11 | 进程失败 | 启动找不到程序，异常退出、stdin EOF、stderr 大量输出、关闭期间崩溃；所有 Task 收敛 |
| V12 | 并发与慢消费 | 多 session 隔离；同 session 第二轮 fail-fast；字节/条数/请求配额满时显式故障、内存有界 |
| V13 | 权限 | selected optionId 校验；拒绝/取消；重复 UI 回答；不默认批准；无 handler 取消 |
| V14 | 文件 | 1-based line/limit，创建新文件，授权根、符号链接、越界和并发关闭 |
| V15 | 终端 | output 与 wait 区别、UTF-8 截断、kill 后仍可读、release 后不可用、session 绑定及清理 |
| V16 | 配置与内容 | modes/configOptions 协商，boolean {} 门控，图像/音频/嵌入资源能力，拒绝未协商 payload |
| V17 | Elicitation/认证 | form/url 能力、accept/decline/cancel、完整 URL 展示/显式打开、连接关联、auth 默认类型及 logout 门控 |
| V18 | 生命周期与日志 | Dispose 幂等、LeaveOpen、OwnsProcess、无遗留子进程；凭据和正文默认不进入日志 |
| V19 | AOT | 实际 publish 后的 Client/Agent 完成双向权限与取消用例；不能只检查 csproj 属性 |

普通 CI 使用确定性 fake agent，无模型账户与公网依赖；不得把 mock echo 通过写成真实模型集成通过。

## 4. 跨语言和实际 Agent 矩阵

| 本方 | 对端 | 传输 | 发布要求 |
|---|---|---|---|
| .NET Client | 官方 TypeScript 示例/协议 fixture Agent | stdio | 1.0 必须 |
| 官方 TypeScript Client fixture | .NET 示例 Agent | stdio | 1.0 必须 |
| .NET Client | 官方 Python Agent fixture | stdio | 1.0 必须 |
| 官方 Python Client fixture | .NET 示例 Agent | stdio | 1.0 必须 |
| .NET Client/Agent | 官方 Rust Client/Agent fixture | stdio | 1.0 必须至少完成两方向主链路及取消 |
| .NET Client/Agent | Java/Kotlin fixtures | stdio | 建议补充，不代替上面核心矩阵 |
| .NET Client | codex-acp、claude-agent-acp 的固定发布/构建 | stdio | 各一组文本、工具权限、取消与其声明的恢复；需显式模型测试环境 |
| TCK | .NET 示例 Agent | stdio/v1 | 辅助门禁；完整报告所有 FAIL/SKIPPED/NOT_TESTED |

互操作夹具应调用对端 SDK 的真实公共 API，不是全部自己拼 JSON。某对端未实现的可选能力记录 N/A；本方已声明能力不可因 fixture 未覆盖就视为通过，需补确定性对端。

真实 Agent 测试不得执行 logout 或操作个人仓库；使用临时工作区和专用测试身份。模型花费与凭据配置由测试环境显式开启，不进入默认 CI。旧实测记录不能代替新 SDK 的互操作验收。

## 5. TCK 的定位

官方 [Testing](https://agentclientprotocol.com/libraries/testing) 指向 [acp-tck](https://github.com/agentclientprotocol/acp-tck)，目前是 experimental，不覆盖全部协议，通过也不是官方兼容认证。它面向 Agent，不能代替 SDK Client 侧的测试。

固定 TCK SHA 后，可按其 README 执行以下形式（示意，.NET Agent 尚未创建）：

```text
uv run acp-tck --report-json report.json -- dotnet path/to/Example.Agent.dll
```

需要附 schema_revision、peer/TCK SHA、协议版本、能力、OS/架构、运行命令和脱敏 transcript。SKIPPED/NOT_TESTED 必须注明原因；本次发现 TCK schema 与最新协议基线不同，因此新字段还需自己的契约测试。

## 6. 稳定 1.0 发布门槛

- 协议来源固定，稳定方法和更新覆盖完整；实验能力不进入默认稳定入口。
- 核心双向互操作通过；真实 Codex/Claude 测试分别提供证据，未通过或未运行时不宣传相应验证。
- Windows/Linux/macOS 的普通运行通过；AOT 至少 Linux x64 与 Windows x64 实际 publish/run，通过后列明支持矩阵，未验证平台不扩称。
- 10 个并发 session、每 session 10,000 条文本/工具更新的确定性压力用例无丢失、无跨会话串流；另测配额溢出与慢消费。记录硬件、消息大小、峰值内存和吞吐，暂不制定未经测量的延迟承诺。
- 所有退出路径的 pending 归零、连接关闭、拥有的子进程回收；测试中没有误杀共享进程。
- NuGet 内容、目标框架、依赖、许可证/NOTICE、符号/SourceLink、XML 文档和 API 差异报告可审查。
- 同一测试控制台覆盖 Client、echo Agent、权限、文件/终端、恢复、泛型扩展等能力，且有相应自动化场景；README 明示本 SDK 为社区实现。
- 只发布一个 OpenAgent.Runtime.Acp 包；nupkg 不包含测试/控制台/CodeGen，不引入其他 OpenAgent 平台依赖。

## 7. 已确定的产品约束与技术探针

| 项目 | 本轮确定的约束 |
|---|---|
| 包与目录 | 单包 OpenAgent.Runtime.Acp，SDK 全部位于 Backend/src/OpenAgent.Runtime.Acp |
| 工程 | SDK、自动化测试、测试控制台三个工程加入主解决方案，只有 SDK 可打包 |
| 功能迭代 | 每一步同步 SDK、测试、控制台和文档，当前状态只记在 TODO |
| 角色 | 同包提供 Client 与 Agent；Processes 是内部可选使用模块 |
| 框架 | net8.0，复用 Backend 的集中配置 |
| 延后范围 | v2、远程传输、代理编排、平台执行链集成 |

RPC 引擎通过 ACP-02 探针决定；AOT 通过实际发布和运行决定支持矩阵。以上约束不再作为是否拆包或选择其他源码目录的待定项。
