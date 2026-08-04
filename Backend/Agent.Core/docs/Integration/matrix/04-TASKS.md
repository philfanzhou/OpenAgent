# Agent.Matrix — 任务清单

## 已完成

- [x] `IAgentConfigProvider` 接口定义（含 `GetConfigAsync` 和 `ListAgentsAsync`）
- [x] `AgentConfig` 完整数据模型（FrameworkType/Llm/Mcp/Rag/Skills/MaxTurns）
- [x] `AgentSummary` 摜要模型
- [x] `LlmConfig` / `McpConfig` / `RagConfig` / `SkillsConfig` 子配置模型
- [x] `McpServerConfig` 支持 SSE/Stdio 两种类型
- [x] `RagInstanceConfig` 支持 ACL 权限控制（AllowedUserIds/Groups/TenantIds/Roles）
- [x] `SkillInstanceConfig` 支持 ACL 权限控制
- [x] `Service` 中 AgentId 多级解析
- [x] `FrameworkType` 多级回退（Config → Context/Header → Mock）

## 待办

- [ ] 补充 `IAgentConfigProvider` 各实现类的文档
- [ ] 补充配置变更通知机制文档
