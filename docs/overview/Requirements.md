# Requirements — OpenAgent

## 服务级需求摘要

| # | 需求 | 详细文档 |
|---|------|----------|
| R-01 | 生产推理统一使用 MAF；Agent 与执行时选择的 LLM Profile 均来自 PostgreSQL 事实源 | [engine.md](../modules/engine.md) |
| R-02 | Pipeline 中间件链（认证、租户校验、追踪、审计） | [execution.md](../modules/execution.md) |
| R-03 | Skill 技能注册与执行 | [capabilities.md](../modules/capabilities.md) |
| R-04 | MCP Server 工具发现与调用 | [capabilities.md](../modules/capabilities.md) |
| R-05 | RAG 知识库检索增强 | [capabilities.md](../modules/capabilities.md) |
| R-06 | 工具调用循环（原生 Function Calling） | [capabilities.md](../modules/capabilities.md) |
| R-07 | PostgreSQL 会话记录与独立文件资产存储 | [conversation.md](../modules/conversation.md) |
| R-08 | 流式推理输出 | [execution.md](../modules/execution.md) |
| R-09 | 统一错误处理与错误码 | [execution.md](../modules/execution.md) |
| R-10 | 安全中间件（认证、权限、租户隔离、AgentId 校验） | [security.md](../modules/security.md) |

## 范围外

- 不负责 HTTP 端点暴露（由 OpenAgent.Engine/OpenAgent.Router 负责）
- 不负责进程生命周期管理（心跳、优雅关闭由 OpenAgent.Engine 负责）
- 不负责 Agent 配置的 CRUD（由 Agent.Matrix 负责）
- 不负责前端 UI（由独立前端项目负责）
