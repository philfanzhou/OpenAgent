# 能力注册（Engine Runtime）

Engine 启动时从 Redis 加载 LLM/RAG 配置；MCP 与 Skill 按 Agent 配置在每次 Agent 执行时创建官方 SDK 资源。

## 核心能力

- **LLM 注册**：从 `llm:published:index` 加载 LlmProviderProfile
- **RAG 注册**：从 `rag:published:index` 加载 RagInstanceConfig
- **Redis 不可用跳过**：所有 Registrar 在 Redis 不可用时静默跳过
- **官方 Skill 包**：对象存储保存 ZIP；ZIP 内使用官方 `SKILL.md` 格式
- **官方 MCP 工具**：使用 MCP C# SDK 的 `McpClientTool`，不复制协议调用层

## 架构

```
IHostedService (启动时)
  ├─ RedisLlmRegistrar      → ILlmRegistry
  └─ RedisRagRegistrar      → IRagRegistry

AgentFactory（按 AgentConfig）
  ├─ McpToolFactory              → official McpClientTool → ChatOptions.Tools
  └─ AgentSkillsProviderFactory  → official AgentSkillsProvider → AIContextProviders
```

## 当前状态

**已实现** — LLM/RAG 仍由 Redis Registrar 热加载；MCP/Skill 的绑定来源是 Agent 配置，执行资源按请求释放。

## 源码位置

- 接口：`Backend/src/OpenAgent.Engine/Abstractions/`
- 实现：`Backend/src/OpenAgent.Engine/Redis/`（RedisLlmRegistrar、RedisRagRegistrar）
- 官方 Skill 适配：`Backend/src/OpenAgent.Core/Capabilities/Skill/`
- 官方 MCP 适配：`Backend/src/OpenAgent.Core/Capabilities/Mcp/`
- 测试替身：`Backend/tests/OpenAgent.Engine.Tests/TestDoubles/`
