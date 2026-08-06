# Agent.Matrix Integration

Agent.Core 以**只读引用**方式访问 Agent.Matrix 的配置与权限数据。原始配置由 `IAgentConfigProvider` 提供，运行前由 `IAgentRuntimeResolver` 汇总解析为已授权、已校验的 `AgentRuntimeProfile`。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 配置读取 | `GetConfigAsync` 按 AgentId 获取运行时配置 |
| 运行配置解析 | `IAgentRuntimeResolver.ResolveAsync` 固定一次调用所需的有效配置 |
| Agent 列表 | `ListAgentsAsync` 返回所有可用 Agent 摘要 |
| 子配置模型 | Llm / Mcp / Rag / Skills 四类子配置 |
| 只读契约 | Agent.Core 绝不向 Matrix 写入数据 |

## Architecture
```text
Agent.Matrix → IAgentConfigProvider → IAgentRuntimeResolver → AgentRuntimeProfile
                                                               ├── Llm       → 已解析模型
                                                               ├── ContextPolicy → 会话压缩策略
                                                               ├── Mcp       → MCP 服务器列表
                                                               ├── Rag       → RAG 实例配置
                                                               ├── Skills    → 技能列表
                                                               └── MaxTurns  → 最大工具调用轮次
```

## Current Status
**Implemented** — 已有原始配置读取与运行前配置解析门面；底层来源仍可替换为独立 Matrix 服务。

## Limits
- 不实现配置变更通知机制（由 Matrix 侧发起变更）
- 不缓存过期 AgentConfig（由 Provider 实现决定策略）
- 不依赖 Matrix 内部实现细节
- 工具调用时仍由 Core 执行二次权限检查

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Configuration/IAgentConfigProvider.cs`, `Backend/src/OpenAgent.Contracts/Configuration/AgentConfig.cs`
- Resolver: `Backend/src/OpenAgent.Core/Runtime/Agent/AgentRuntimeResolver.cs`
