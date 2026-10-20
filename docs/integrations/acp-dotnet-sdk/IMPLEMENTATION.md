# 八阶段实现任务与验收

每阶段同步交付 SDK、自动化测试及文档。下表描述实现任务与验收入口；当前状态仅见 [任务清单](../../planning/TODO.md)。本次按用户要求合并为一个可独立评审的新 PR；提交仍按基础、交互、验证分层，避免将阶段相互依赖拆成不能运行的包。

| 阶段 | 功能任务 | 自动化验收 |
|---|---|---|
| P0 ACP-00 | 单包工程、Stream NDJSON、所有权、32 MiB 帧上限 | 分片/CRLF/EOF、完整写入、取消、依赖边界、进程退出码 |
| P1 ACP-01 | 手写 DTO、判别联合、STJ 元数据、RpcId、补丁 presence、固定 schema 来源 | 摘要/14 更新/五类内容/null 与整数 ID/扩展内存/普通 getter 分配回归 |
| P2 ACP-02 | 双向重入、关联、batch、标准错误、一般取消、配额、统一关闭、诊断 | 嵌套请求/100 并发关联/无响应通知/batch 后立即取消/EOF/额度耗尽 |
| P3 ACP-03 | 初始化、能力门控、Client/Agent、Session/Turn、早到更新、取消确认 | 内存连接的流式中文/表情、早到通知、配额、未确认取消、慢消费 |
| P4 ACP-04 | ArgumentList、环境策略、进程租约、stderr、退出码、关停宽限 | 自有进程启动、非零退出、stderr 排空、释放击杀（ProcessWorker 辅助子进程） |
| P5 ACP-05 | load/resume/list/close/delete、modes/configOptions、14 类更新 | 回放先于响应/resume 无回放/分页接口/配置类型/更新隔离 |
| P6 ACP-06 | 权限、文件、终端、form/url、agent/terminal auth、logout | 默认拒绝/非法选择/审批取消/路径与符号链接/UTF-8/表单结果/资源回收 |
| P7 ACP-07 | 版本与升级策略、单包发布边界 | SDK 依赖门禁；互通、AOT、压力与包检查脚手架按用户要求移出本 PR，恢复时另行实现并记录实测 |

依赖顺序为 P0 → P1 → P2 → P3 → P4 → P5 → P6 → P7。P1 调整理由与目录见 [架构文档](DESIGN.md)；不恢复旧通用 schema 生成器。SDK 选择小型自有 RPC 以直接控制 reader 重入、取消确认和 AOT 元数据，避免向公开 API 引入 RPC 第三方类型；本次未实现 StreamJsonRpc 比较基准，不以未执行的基准支撑性能优劣判断。

官方 SDK 互通、NativeAOT 实测与包检查脚本已按用户要求移出本 PR。这些验证不能由静态 DTO 或 AOT 属性推断；如需恢复须重新实现脚手架并分别记录实测结果。稳定版发布前还需验收宿主授权、持久化策略与真实 Agent；本 PR 版本保持 preview，不发布 NuGet。
