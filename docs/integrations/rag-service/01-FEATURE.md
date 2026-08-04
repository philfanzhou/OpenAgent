# RAG Service — 功能概述

## 核心能力

Agent.Core 通过 RAG（Retrieval-Augmented Generation）服务实现知识检索，为 Agent 提供外部知识支持。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `IRagService` | `src/Core/Abstract/IRagService.cs` | RAG 服务统一抽象 |
| `IRagAdapter` | `Agent.Contracts/Models/IRagAdapter.cs` | RAG 后端适配器接口 |
| `RagService` | `src/Core/Capabilities/Rag/RagService.cs` | RAG 服务实现 |
| `QdrantAdapter` | `src/Core/Capabilities/Rag/Adapters/QdrantAdapter.cs` | Qdrant 向量数据库适配器 |
| `RagFlowAdapter` | `src/Core/Capabilities/Rag/Adapters/RagFlowAdapter.cs` | RagFlow 适配器 |
| `RagSearchTool` | — | RAG 搜索工具（注入为 Agent 工具） |
| `SearchResult` | `Agent.Contracts/Models/` | 检索结果模型 |
| `RagInstanceConfig` | `Agent.Contracts/Configuration/AgentConfig.cs` | RAG 实例配置 |

## 支持的后端

| 后端 | AdapterName | 说明 |
|------|-------------|------|
| Qdrant | `"qdrant"` | 向量数据库，支持语义检索与索引 |
| RagFlow | `"ragflow"` | RAG 平台，支持文档解析与检索（不支持索引） |

## 集成方式

RAG 检索通过 `RagSearchTool` 集成为 Agent 工具（工具名 `search_knowledge_base`），Agent 可自主决定何时检索知识。

## IRagService 核心方法

```csharp
Task IndexDocumentAsync(string content, Dictionary<string, object>? metadata = null,
    string? ragInstanceId = null, CancellationToken ct = default);
Task<List<string>> SearchAsync(string query, int limit = 3,
    RagConfig? overrideConfig = null, CancellationToken ct = default);
Task<List<SearchResult>> SearchDetailedAsync(string query, int limit = 3,
    RagConfig? overrideConfig = null, CancellationToken ct = default);
```
