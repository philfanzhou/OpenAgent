# ACP .NET SDK 资料与规格

整理日期：2026-10-16（Asia/Shanghai）。状态：**C# SDK v0.3 已改为单包设计；P0 传输、测试与控制台基线已本地验证，完整协议逐阶段实现**。

这里的 ACP 指 **Agent Client Protocol**。唯一 SDK 为 Backend/src/OpenAgent.Runtime.Acp，自动化测试和测试控制台独立工程但不发布为 SDK 包。

| 文档 | 用途 |
|---|---|
| [SPEC.md](./SPEC.md) | 主规格：产品目标、功能矩阵、公共 API、运行机制与版本策略 |
| [DESIGN.md](./DESIGN.md) | 架构设计：同包内部模块、组件协作、消息流程、能力与资源归属 |
| [DIRECTORY.md](./DIRECTORY.md) | 目录设计：主解决方案内的 SDK、测试、控制台与生成器 |
| [IMPLEMENTATION.md](./IMPLEMENTATION.md) | 八个阶段及子任务，每一步同步 SDK、自动化测试和控制台 |
| [TEST-CONSOLE.md](./TEST-CONSOLE.md) | 测试控制台当前运行方法、后续场景及结果契约 |
| [COMPARISON.md](./COMPARISON.md) | 2026-10-16 复核：官方/社区架构、功能覆盖、.NET 缺口与借鉴方向 |
| [DEMO.md](./DEMO.md) | 最小 C# Client/Agent 的运行、取消、接入与验证说明 |
| [RESEARCH.md](./RESEARCH.md) | 官网结论、五种官方 SDK、实际适配案例及社区 .NET 实现对比 |
| [PROTOCOL.md](./PROTOCOL.md) | v1 方法、能力、数据类型和错误语义的覆盖规格 |
| [VALIDATION.md](./VALIDATION.md) | 分阶段交付范围、互操作矩阵与发布门槛 |
| [SOURCES.md](./SOURCES.md) | 带固定提交链接的阅读清单及一手资料 |
| [sources.json](./sources.json) | 调研源码 SHA、schema 哈希与机器可读方法清单 |
| [architecture-sources.json](./architecture-sources.json) | 10 月 16 日架构对比所用的固定提交与关键源码哈希 |

建议先读 SPEC，再按 DESIGN / DIRECTORY 评审工程设计；协议细节查 PROTOCOL，交付门槛查 VALIDATION，设计依据查 COMPARISON。实现模型以固定提交的官方稳定 schema 为准。

阶段任务的当前状态统一见 [任务清单](../../planning/TODO.md)；本地验证不等于已合并、完整 SDK 发布或真实模型互操作通过。
