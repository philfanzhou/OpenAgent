# RAG Capability

RAG 在 Agent.Core 中作为模型可调用的工具参与执行，是否被调用由模型和工具链路共同决定。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 文档索引 | 将内容索引到外部 RAG 系统 |
| 语义检索 | 从外部 RAG 系统检索相关文档 |
| 多实例支持 | 同时配置和使用多个 RAG 实例 |
| 适配器扩展 | 通过 `IRagAdapter` 支持不同 RAG 产品 |
| ACL 权限过滤 | 基于用户上下文过滤可见实例 |

## Implemented Adapters
| Adapter | Type | Index | Search |
|---------|------|-------|--------|
| QdrantAdapter | `qdrant` | 是（需嵌入模型） | 是 |
| RagFlowAdapter | `ragflow` | 否 | 是 |

## Architecture
```text
Agent → ToolCall("search_knowledge_base")
  → RagCapabilitySource
  → IRagService.SearchAsync/SearchDetailedAsync
  → IRagAdapter → RAG Backend
```

## Current Status
**Implemented** — 多实例检索、适配器扩展、权限过滤均已落地。

## Limits
- Qdrant 索引请求中向量字段使用空数组占位，实际使用需调用嵌入模型
- RagFlowAdapter 不支持索引
- 适配器响应解析使用同步 `.GetAwaiter().GetResult()`
- 无检索结果缓存

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Rag/RagCapabilitySource.cs`, `Backend/src/OpenAgent.Core/Capabilities/Rag/RagService.cs`, `Backend/src/OpenAgent.Core/Capabilities/Rag/Adapters/`
- Contracts: `Backend/src/OpenAgent.Contracts/Models/IRagAdapter.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/RagCapabilitySourceTests.cs`
