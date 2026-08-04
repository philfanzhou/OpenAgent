# Agent.Engine 概览文档

本目录包含 Agent.Engine 服务的概览级文档，帮助读者快速理解服务的定位、边界与核心机制。

> 详细实现文档请参阅 [../modules/](../modules/README.md) 和 [../Integration/](../Integration/README.md)。

## 文档索引

| 文档 | 内容 | 阅读时长 | 前置依赖 |
|------|------|----------|----------|
| [SystemContext](./SystemContext.md) | 服务定位、上下游关系、职责边界 | 5 min | 无 |
| [Integration](./Integration.md) | 外部交互矩阵、故障语义 | 8 min | SystemContext |
| [KeyFlows](./KeyFlows.md) | 核心业务流程时序图 | 10 min | SystemContext |
| [DataOwnership](./DataOwnership.md) | 数据实体所有权与读写权限 | 5 min | SystemContext |
| [Requirements](./Requirements.md) | 服务级需求摘要 | 5 min | SystemContext |
| [Design](./Design.md) | 服务级架构概述、技术栈、统一约束 | 5 min | SystemContext |
| [coding-conventions.md](.agent/rules/coding-conventions.md) | .NET 编码规范 | 15 min | 无 |

## 推荐阅读路径

```
SystemContext ──> Integration ──> KeyFlows ──> DataOwnership ──> Requirements
                                                                      |
`.agent/rules/coding-conventions.md` <── (独立，随时可读) ────────────────+
```

1. **新成员入职**：按顺序阅读 SystemContext → KeyFlows → DataOwnership → Requirements
2. **集成对接**：直接阅读 Integration + DataOwnership
3. **编码开发**：`.agent/rules/coding-conventions.md` + KeyFlows
4. **运维排障**：SystemContext + Integration（故障语义部分）
