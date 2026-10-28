# OpenAgent 文档中心

## 主要入口

| 入口 | 解决的问题 |
|---|---|
| [开发指南](overview/DevelopmentGuide.md) | 改哪个模块、接口如何协作、如何验证 |
| [任务清单](planning/TODO.md) | P0/P1/P2、剩余拆分、已有 PR 集成、分派与验收 |
| [系统上下文](overview/SystemContext.md) → [运行时设计](overview/Design.md) → [关键流程](overview/KeyFlows.md) | 当前服务、执行链与边界 |
| [部署指南](deployment.md) | 本地启动、认证、环境变量、数据卷与镜像更新 |

## 按工作内容查阅

| 内容 | 文档 |
|---|---|
| HTTP/SSE 契约 | [API](overview/API.md) |
| 功能实现与边界 | [功能域索引](modules/README.md) |
| 外部系统联调 | [集成索引](integrations/README.md) |
| 数据模型与主责 | [数据所有权](overview/DataOwnership.md)、[数据库索引](database/README.md) |
| 日志与排障 | [可观测性](overview/Observability.md)、[追踪排查](trace-troubleshoot.md) |
| 并行环境 | [并行预览](parallel-previews.md) |

## 背景与规范

- [架构决策](decisions/README.md)、[历史方案](archive/README.md)、[验证记录](test-reports/README.md)。历史文档中的状态与清单仅记录当时情况。
- [文档规范](../.agent/rules/doc-standards.md)、[编码规范](../.agent/rules/coding-conventions.md)、[AI 工作流](../.agent/README.md)。
