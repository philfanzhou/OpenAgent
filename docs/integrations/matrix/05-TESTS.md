# Agent.Matrix — 测试清单

## 单元测试

### IAgentConfigProvider

- `GetConfigAsync()` 无参调用返回默认配置
- `GetConfigAsync(agentId)` 按 AgentId 返回对应配置
- `GetConfigAsync(agentId)` AgentId 不存在时返回 null
- `ListAgentsAsync()` 返回所有可用 Agent 摘要

### AgentConfig 模型

- `FrameworkType` 默认值为 `MAF`
- `MaxTurns` 默认值为 50
- `Llm.Temperature` 默认值为 0.7
- `Llm.ModelId` 默认值为 "gpt-4o"
- `Rag.Enabled` 默认值为 false

### Service 中的配置使用

- AgentId 解析优先级（context → Header → Items → default）
- FrameworkType 解析优先级（Config → Context/Header → Mock）
- 配置不存在时抛出 `InvalidOperationException`
- `MaxTurns` 为 0 时使用默认值 5

## 集成测试

- 配置获取端到端流程
- 多 Agent 配置隔离
