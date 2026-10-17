# ACP .NET SDK

社区实现的 Agent Client Protocol v1 SDK，唯一发布包为 `OpenAgent.Runtime.Acp`。已交付 Stream framing、传输生命周期（P0）与稳定 v1 协议模型、可重复代码生成（P1：178 个固定定义、25 方法、14 更新的同包 DTO/STJ context/信封读取规则）；RPC 引擎、Client/Agent 角色、进程和交互能力按阶段实现，状态与负责人见 [TODO](../../planning/TODO.md)。

| 文档 | 唯一职责 |
|---|---|
| [SPEC](./SPEC.md) | 产品边界、API 行为、配额与取消 |
| [DESIGN](./DESIGN.md) / [DIRECTORY](./DIRECTORY.md) | 内部依赖、资源归属 / 工程位置 |
| [PROTOCOL](./PROTOCOL.md) / [sources.json](./sources.json) | wire 规则 / 固定 v1 来源与哈希 |
| [IMPLEMENTATION](./IMPLEMENTATION.md) | 八阶段交付与前置 |
| [TEST-CONSOLE](./TEST-CONSOLE.md) | 控制台命令与报告契约 |
| [VALIDATION](./VALIDATION.md) | 验收用例与发布门槛 |

变更基于 `dev`，PR 目标为 `dev`，沿用 .NET 10 集中配置。只保留正式 SDK、自动化测试及 SDK 测试控制台；原始验证日志通过任务附件交付。
