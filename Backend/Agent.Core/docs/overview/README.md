# Agent.Core 总览文档

本目录包含 Agent.Core 服务的总览级正式文档，负责帮助读者快速建立全局认知和找到下钻入口。

> 本目录只负责全局认知、边界和导航，不替代 `modules/`、`Integration/`、`database/` 的细节文档。

## 文档清单

| 文档 | 用途 | 阅读场景 |
|------|------|----------|
| [SystemContext.md](./SystemContext.md) | 服务定位、上下游、参与者 | 首次了解本服务在系统中的位置 |
| [Integration.md](./Integration.md) | 集成矩阵、接口边界、失败语义 | 联调、排障外部依赖 |
| [KeyFlows.md](./KeyFlows.md) | 关键跨服务时序与调用链 | 理解核心业务流程 |
| [DataOwnership.md](./DataOwnership.md) | 数据主责、引用边界、双写禁区 | 数据库变更、数据迁移 |
| [Requirements.md](./Requirements.md) | 服务级需求摘要 | 了解功能范围 |
| [Design.md](./Design.md) | 服务级架构概述 | 了解分层、技术栈、依赖关系 |
| [coding-conventions.md](.agent/rules/coding-conventions.md) | 代码规范 | 开发前了解编码约定 |

## 阅读建议

1. **首次阅读**：SystemContext → Design → KeyFlows → Requirements
2. **联调排障**：Integration → KeyFlows → 对应 modules/ 文档
3. **数据变更**：DataOwnership → database/ 文档
4. **开发执行**：Design → `.agent/rules/coding-conventions.md` → 对应 modules/ 文档
