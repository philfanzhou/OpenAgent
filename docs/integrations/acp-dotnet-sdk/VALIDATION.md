# SDK 验收

每阶段同时验收 SDK、测试、控制台、进程测试和文档，按 [IMPLEMENTATION](./IMPLEMENTATION.md) 顺序独立复核。候选运行主解决方案 build/test、对应控制台及适用的 pack；共享 RPC/生命周期变化回归前阶段。报告包含固定 SHA、OS/架构、命令、实际数字/退出码、跳过原因、资源清理及未覆盖范围。原始日志交任务附件，状态只记 [TODO](../../planning/TODO.md)。

## 行为矩阵

| 编号 | 必须验证 |
|---|---|
| V01 模型 | 固定 schema 全部方法/分支；必填、默认、nullable、int64、unknown 与兼容读取 |
| V02 framing | 半包/多包/长行、UTF-8 分片、转义换行、CRLF、stderr 隔离；非法 JSON 在 RPC 可恢复 |
| V03 RPC | string/int64/null/缺失 ID、未知方法、单/混合/空 batch、通知无响应、重复/迟到响应 |
| V04 initialize | 版本不匹配、缺省能力、{} 与 null、未初始化调用、虚假能力拒绝 |
| V05 重入 | prompt 等待反向调用期间响应/cancel 可达，无 reader 死锁 |
| V06 会话 | new 唯一、load 先回放、resume 无回放、分页、close/delete 差异 |
| V07 早到更新 | new/load 响应前不丢失、不误绑；未绑定可观测且缓冲有界 |
| V08 更新 | 全部 14 类、消息/工具/压缩各自关联；最终响应不越过已提交更新入队 |
| V09 取消 | prompt+权限+terminal wait、尾部更新、自然完成竞态、仅一次最终响应 |
| V10 无确认取消 | 忽略取消后宽限超时、未确认异常、连接关闭、无假 cancelled/无限 pending |
| V11 进程故障 | 命令缺失、非零退出、stdin EOF、大量 stderr、关闭崩溃；全部 Task 收敛 |
| V12 流控 | 多 session 隔离、同 session fail-fast、字节/条数/请求配额与慢消费，内存有界 |
| V13 权限 | optionId、拒绝/取消、重复回答、缺省不批准 |
| V14 文件 | 1-based line/limit、创建、授权根、真实路径/符号链接、越界/并发关闭 |
| V15 终端 | output/wait 区别、UTF-8 字节截断、kill 后可读、release 后失效、session 清理 |
| V16 配置/内容 | modes/configOptions、boolean {} 门控、图像/音频/嵌入资源能力，拒绝未协商 payload |
| V17 交互/认证 | form/url、accept/decline/cancel、URL 完整展示/显式打开、连接归属、auth 默认类型/logout 门控 |
| V18 生命周期/日志 | Dispose 幂等、LeaveOpen/OwnsProcess、无自有进程泄漏/共享误杀，凭据/正文默认不入日志 |
| V19 AOT | 实际 publish 产物双侧完成权限与取消，不由 STJ context 或 csproj 属性推断 |

ACP-02 用同组 wire 探针对比 StreamJsonRpc/自有实现：ID/重入/取消/batch/错误/顺序、适配量/依赖/API、关闭收敛与实际 AOT/裁剪。成熟库行为完整且适配更小则优先；结果形成 ADR，不预设结论。

## 1.0 发布门槛

- 固定 schema/meta 来源、SHA/哈希/许可及可重复生成；全部稳定 v1 方法/更新覆盖，实验功能不进入默认入口。
- 官方 TypeScript、Python、Rust SDK 固定版本，与 .NET Client/Agent 两方向完成主链路及取消；fixture 调对端真实公共 API，不全部手拼 JSON。Java/Kotlin 建议补充。可选对端能力缺失记 N/A；本方声明能力另补确定性测试。
- 固定 codex-acp、claude-agent-acp 各测文本、工具权限、取消及所声明恢复；专用临时环境，模型/身份/花费显式授权，不 logout 或操作个人仓库。未测不得宣传真实模型验证。
- TCK 仅辅助 Agent 侧验收，固定版本并报告 FAIL/SKIPPED/NOT_TESTED，不称官方认证或代替 Client 测试。互操作证据含 schema/peer/TCK SHA、协议/能力、平台、命令及脱敏 transcript。
- Windows/Linux/macOS 普通运行；AOT 至少 Linux x64、Windows x64 实际 publish/run，支持范围只列已验证平台。
- 10 并发 session，各 10,000 条文本/工具更新，无丢失或串流；另测慢消费/配额溢出。记录硬件、消息大小、峰值内存、吞吐及原始参数，不预设未经测量的延迟承诺。
- 所有退出路径 pending 归零、连接关闭、自有进程回收，未误杀共享进程；默认离线用例不依赖模型/公网。
- nupkg 仅一个 SDK 程序集，无测试/控制台/CodeGen 或平台依赖；TFM、许可/NOTICE、SourceLink/符号、XML/API 差异、升级说明可审查。README 标明社区实现。
- 控制台覆盖已声明能力并有对应自动化；发布与合并另行授权。本机门控跳过不算跨平台/沙箱/互操作通过；纯 SDK 无服务镜像时说明 Preview/Docker 不适用，不以其他验证冒充。
