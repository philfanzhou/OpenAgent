# 能力注册（Engine Runtime）

LLM 不再通过启动期 Registrar 或内存 Registry 注册，而是在每次执行时按租户和 `llmProfileId` 从 PostgreSQL/Redis TTL 缓存解析。

RAG 与 MCP 仍使用现有目录边界；MCP 和 Skill 按 Agent 保存的 ID 在执行时创建官方 SDK 资源。

```text
AgentFactory
  ├─ ILlmConfigProvider          → selected model profile
  ├─ McpToolFactory              → official McpClientTool
  └─ AgentSkillsProviderFactory  → official AgentSkillsProvider
```

LLM Profile 的租户隔离由持久化主键、Redis key 和执行时的已验证 tenantId 三层共同约束。
