# Agent.Matrix — 功能概述

## 核心能力

Agent.Core 以 **只读引用** 方式访问 Agent.Matrix 的配置与权限数据，通过 `IAgentConfigProvider` 获取 Agent 运行时所需的全部配置。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `IAgentConfigProvider` | `Agent.Contracts/Configuration/IAgentConfigProvider.cs` | Agent 配置提供者接口 |
| `AgentConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | Agent 运行时配置模型 |
| `AgentSummary` | `Agent.Contracts/Configuration/IAgentConfigProvider.cs` | Agent 摘要信息 |
| `LlmConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | LLM 配置 |
| `McpConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | MCP 配置 |
| `RagConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | RAG 配置 |
| `SkillsConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | 技能配置 |

## 数据流向

```
Agent.Matrix → IAgentConfigProvider → AgentConfig
                                        ├── FrameworkType    → 决定引擎类型
                                        ├── Llm             → LLM 连接配置
                                        ├── Mcp             → MCP 服务器列表
                                        ├── Rag             → RAG 实例配置
                                        ├── Skills          → 技能列表
                                        └── MaxTurns        → 最大工具调用轮次
```

- Agent.Core 通过 `IAgentConfigProvider` 获取配置
- Agent.Core **绝不** 向 Matrix 写入任何数据

## IAgentConfigProvider 核心方法

```csharp
Task<AgentConfig> GetConfigAsync(CancellationToken ct = default);
Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken ct = default);
Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken ct = default);
```
