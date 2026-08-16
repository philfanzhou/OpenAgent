# Requirements — OpenAgent

## 服务级需求摘要

| # | 需求 | 详细文档 |
|---|------|----------|
| R-01 | 生产推理统一使用 MAF，并保留 MockAgent 降级解析器（AllowMockAgent）和旧配置兼容 | [modules/engine/](../modules/engine/) |
| R-02 | Pipeline 中间件链（认证、租户校验、追踪、审计） | [modules/execution/pipeline/](../modules/execution/pipeline/) |
| R-03 | Skill 技能注册与执行 | [modules/capabilities/skill/](../modules/capabilities/skill/) |
| R-04 | MCP Server 工具发现与调用 | [modules/capabilities/mcp/](../modules/capabilities/mcp/) |
| R-05 | RAG 知识库检索增强 | [modules/capabilities/rag/](../modules/capabilities/rag/) |
| R-06 | 工具调用循环（原生 Function Calling） | [modules/capabilities/tool-calling/](../modules/capabilities/tool-calling/) |
| R-07 | PostgreSQL 会话记录与独立文件资产存储 | [modules/conversation/store/](../modules/conversation/store/) |
| R-08 | 流式推理输出 | [modules/execution/streaming/](../modules/execution/streaming/) |
| R-09 | 统一错误处理与错误码 | [modules/execution/errors/](../modules/execution/errors/) |
| R-10 | 安全中间件（认证、权限、租户隔离、AgentId 校验） | [modules/security/](../modules/security/) |

## 范围外

- 不负责 HTTP 端点暴露（由 OpenAgent.Engine/OpenAgent.Router 负责）
- 不负责进程生命周期管理（心跳、优雅关闭由 OpenAgent.Engine 负责）
- 不负责 Agent 配置的 CRUD（由 Agent.Matrix 负责）
- 不负责前端 UI（由独立前端项目负责）
