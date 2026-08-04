# OpenAgent 文档中心

> 本目录是 OpenAgent 平台的统一文档入口。源码目录不再散落文档。

## 目录结构

| 目录 | 内容 |
|------|------|
| [overview/](./overview/) | 总览文档（系统上下文、集成、流程、需求、设计、数据所有权） |
| [modules/](./modules/) | 功能域详细文档（execution、conversation、capabilities、security、engine） |
| [integrations/](./integrations/) | 外部依赖集成（LLM、Redis、SQL、MCP、RAG） |
| [database/](./database/) | 数据存储唯一事实源 |
| [decisions/](./decisions/) | 架构决策归档（ADR） |
| [planning/](./planning/) | 规划文档（目标架构、重构基线） |
| [review-archive/](./review-archive/) | 历史审阅记录 |

## 阅读路径

1. **首次阅读**：`overview/README.md` → `overview/Design.md` → `overview/KeyFlows.md`
2. **功能开发**：`modules/` → 对应功能域
3. **集成联调**：`integrations/` → 对应外部依赖
4. **数据库变更**：`database/`
5. **了解决策背景**：`decisions/`

## 文档规范

- 文档风格与新增指南：`.agent/rules/doc-standards.md`
- 编码规范：`.agent/rules/coding-conventions.md`
