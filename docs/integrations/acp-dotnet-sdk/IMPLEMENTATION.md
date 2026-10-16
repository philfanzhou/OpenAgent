# 阶段交付契约

每个能力必须在同一可评审变更中同步交付 **SDK → 自动化测试 → 控制台场景 → 真实控制台进程测试 → 文档及回归**。至少覆盖正常、非法/拒绝和适用的取消/关闭；记录命令、环境、固定 SHA、通过/失败/跳过与未覆盖范围。控制台只用 SDK 公共 API，不以生成类型或成功字样代替验收。

状态、负责人和实际结果只维护在 [TODO](../../planning/TODO.md)。以下为目标，依序 ACP-00 → 01 → 02 → 03 → 04 → 05 → 06 → 07；每阶段须独立审查及技术负责人核对交接分支/SHA/PR后才启动下一项，同时仅一个阶段写 SDK。所有 PR 基于并指向 dev；不自动合并、发包或部署。

| 阶段 | SDK 与自动化验收重点 | 控制台交付 |
|---|---|---|
| ACP-00 / P0 | 唯一包、IAcpTransport/AcpStreamTransport、字节上限、LeaveOpen；UTF-8 分片、CRLF/多帧、半帧 EOF、并发完整写入、写前/写中取消、释放及平台依赖禁用。三工程入主解，pack 仅 SDK 可产包 | list、framing、all；0/1/2 退出码，错误命令非零，真实进程测试 |
| ACP-01 / P1 | 固定稳定 v1 schema/meta/lock、来源/哈希/许可；可重复 CodeGen、DTO/方法/STJ context/RpcId/错误；必填/默认/null/{}、联合、int64、_meta 生命周期及非法读取。重复生成无差异，覆盖全部稳定方法/更新；不宣称可调用 | schema；结构化报告及进程测试 |
| ACP-02 / P2 | 同组探针对比 StreamJsonRpc/自有引擎；双向 reader/dispatcher/pending/writer，request/notification/batch、错误/扩展、取消/配额/统一关闭。嵌套反向调用、同号双向 ID、重复迟到响应、慢 handler、EOF/超限均收敛，无遗留任务（V02/03/05/10/12/18） | rpc、reverse、fault，确定性故障注入 |
| ACP-03 / P3 | 角色 API、builder/handler、initialize/能力门控；new/prompt/update/cancel、Session/Turn、文本/资源链接和默认拒绝权限；早到通知、尾部更新、会话可复用/关闭。真实 stdio 双进程中文/表情流式及取消 | agent echo、client、echo、cancel；stdout 仅协议，诊断走 stderr |
| ACP-04 / P4 | executable+ArgumentList、cwd/环境、transport/ProcessLease、持续 stderr、退出/所有权/有界关停；命令缺失、非零退出、大量 stderr、崩溃和慢 IO，无请求/自有进程泄漏，session 不杀共享进程 | process，外部 Agent 参数、cwd/超时；握手与真实模型结果分别记录 |
| ACP-05 / P5 | load 回放先于响应、resume 无回放、list 分页、close/delete；modes/configOptions/boolean 能力及轮次中配置；14 类更新、全部内容、顺序/关联/多 session/慢消费（V06–08/12/16）。内存存储不宣称生产持久化 | session、config、updates，分别可重现 |
| ACP-06 / P6 | 按下表逐项同步交付（V09/13–18），未注册能力不公布 | 每项独立场景 |
| ACP-07 / P7 | 固定官方 TS/Python/Rust SDK 双向互操作、TCK 辅助；压力/慢消费/关闭；实际跨平台及 AOT publish/run；NuGet 内容/依赖/许可/SourceLink/XML/API 变更与升级文档，按 VALIDATION 发布门槛验收 | interop、load、版本/指标/报告汇总 |

ACP-06 按顺序逐项完成，不等最后补测试：

| 子项 | 验收重点 | 场景 |
|---|---|---|
| 权限 | 有效 optionId、一次性选择/取消、默认拒绝、重复回答/竞态/跨 session | permission |
| 文件 | 1-based line/limit、创建、真实路径/授权根、符号链接/越界/并发关闭 | files（临时目录） |
| 终端 | 五方法、session 绑定、UTF-8 截断、kill 保留输出、release 失效、关闭回收 | terminal（自有进程） |
| Elicitation | form/url 协商、accept/decline/cancel、schema、connection 关联 | elicitation（模拟 UI） |
| 认证 | 方法缺省类型、authenticate、错误/取消/脱敏、logout 显式门控 | auth（确定性身份） |

共享 RPC/生命周期改动须回归所有已交付阶段；合入候选运行主解决方案 build/test 和对应控制台。具体行为与发布门槛分别见 [SPEC](./SPEC.md)、[PROTOCOL](./PROTOCOL.md)、[VALIDATION](./VALIDATION.md)。
