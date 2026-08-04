# RAG Service

Agent.Core 通过 RAG 服务实现知识检索，为 Agent 提供外部知识支持。RAG 检索通过 `RagSearchTool` 集成为 Agent 工具（`search_knowledge_base`），由模型自主决定何时检索。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 文档索引 | 将内容索引到外部 RAG 系统 |
| 语义检索 | 从外部 RAG 系统检索相关文档 |
| 多实例支持 | 同时配置和使用多个 RAG 实例 |
| 适配器扩展 | 通过 `IRagAdapter` 支持不同 RAG 产品 |
| ACL 权限过滤 | 基于用户上下文过滤可见实例 |

## Supported Backends
| Backend | AdapterName | Index | Search |
|---------|-------------|-------|--------|
| Qdrant | `qdrant` | 是（需嵌入模型） | 是 |
| RagFlow | `ragflow` | 否 | 是 |

## Architecture
```text
IRagService (RagService)
  ├── IEnumerable<IRagAdapter>（QdrantAdapter / RagFlowAdapter）
  ├── IAgentConfigProvider → RagConfig
  ├── IRagRegistry → 全局 RAG 实例
  └── IHttpClientFactory → HTTP 客户端
```

## Current Status
**Implemented** — 多实例检索、适配器扩展、权限过滤均已落地。

## Limits
- Qdrant 索引请求中向量字段使用空数组占位，实际使用需调用嵌入模型
- RagFlowAdapter 不支持索引
- 适配器响应解析使用同步 `.GetAwaiter().GetResult()`

## Source
- Core: `src/Core/Capabilities/Rag/RagService.cs`, `Adapters/QdrantAdapter.cs`, `Adapters/RagFlowAdapter.cs`
- Contracts: `Agent.Contracts/Models/IRagAdapter.cs`
- Tests: `test/OpenAgent.Core.Tests/Rag/`
