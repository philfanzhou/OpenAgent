# Agent.Matrix Integration

Agent.Core 以**只读引用**方式访问 Agent.Matrix 的配置与权限数据，通过 `IAgentConfigProvider` 获取 Agent 运行时所需的全部配置。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 配置读取 | `GetConfigAsync` 按 AgentId 获取运行时配置 |
| Agent 列表 | `ListAgentsAsync` 返回所有可用 Agent 摘要 |
| 子配置模型 | Llm / Mcp / Rag / Skills 四类子配置 |
| 只读契约 | Agent.Core 绝不向 Matrix 写入数据 |

## Architecture
```text
Agent.Matrix → IAgentConfigProvider → AgentConfig
                                        ├── FrameworkType → 引擎类型
                                        ├── Llm           → LLM 连接配置
                                        ├── Mcp           → MCP 服务器列表
                                        ├── Rag           → RAG 实例配置
                                        ├── Skills        → 技能列表
                                        └── MaxTurns      → 最大工具调用轮次
```

## Current Status
**Implemented** — 接口与数据模型完整，支持 AgentId 多级解析和 FrameworkType 回退。

## Limits
- 不实现配置变更通知机制（由 Matrix 侧发起变更）
- 不缓存过期 AgentConfig（由 Provider 实现决定策略）
- 不依赖 Matrix 内部实现细节

## Source
- Contracts: `Agent.Contracts/Configuration/IAgentConfigProvider.cs`, `AgentConfig.cs`
- Core consumer: `src/Core/Execution/Service.cs`
