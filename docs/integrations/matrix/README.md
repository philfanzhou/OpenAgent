# 配置目录与 Matrix 接入边界

Application 以只读方式访问运行配置，不引用 Engine 的实现。当前 `IAgentConfigProvider` 由 Engine 的 `ConfigurationService` 实现，运行前由 Application 的 `IAgentRuntimeResolver` 汇总解析为已授权、已校验的 `AgentRuntimeProfile`；没有独立的 Agent.Matrix 服务。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 配置读取 | `GetConfigAsync` 按 AgentId 获取运行时配置 |
| 运行配置解析 | `IAgentRuntimeResolver.ResolveAsync` 固定一次调用所需的有效配置 |
| Agent 列表 | `ListAgentsAsync` 返回所有可用 Agent 摘要 |
| 子配置模型 | Llm / ContextPolicy / Mcp / Rag / Skills / MaxTurns 等运行时子配置 |
| 只读契约 | Application 执行链只读取配置；管理端点负责配置写入 |

## Architecture
```text
Engine.ConfigurationService → IAgentConfigProvider → IAgentRuntimeResolver → AgentRuntimeProfile
                                                               ├── Llm       → 已解析模型
                                                               ├── ContextPolicy → 会话压缩策略
                                                               ├── Mcp       → MCP 服务器列表
                                                               ├── Rag       → RAG 实例配置
                                                               ├── Skills    → 技能列表
                                                               └── MaxTurns  → 最大工具调用轮次
```

## Current Status
**Implemented** — 已有配置读取与运行前解析门面。Matrix 是底层来源的后续可替换方向，不代表当前部署组件。

## Limits
- 配置持久化与 TTL 缓存分别由 Framework 和 Engine 实现，Application 不另建缓存
- 配置写入、缓存失效与运行时读取约定见 [配置管理链路](../../modules/engine/runtime/config-management/DESIGN.md)
- 不依赖 Matrix 内部实现细节
- 工具发现与调用由 Application 的适配层接入 Capabilities/Security，执行时复核权限

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Configuration/IAgentConfigProvider.cs`, `Backend/src/OpenAgent.Contracts/Configuration/AgentConfig.cs`
- Resolver: `Backend/src/OpenAgent.Application/Execution/AgentRuntimeResolver.cs`
