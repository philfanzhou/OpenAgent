# RAG Service

外部知识检索通过 RAG 服务接入：`RagCapabilitySource` 把检索暴露为 `search_knowledge_base` 工具，由模型自主决定何时检索。
能力实现、调用链与限制见 [RAG Capability](../../modules/capabilities/rag/README.md)，本文件只记录外部系统侧的接入事实。

## 支持的后端

| Backend | AdapterName | Index | Search |
|---------|-------------|-------|--------|
| Qdrant | `qdrant` | 是（需嵌入模型） | 是 |
| RagFlow | `ragflow` | 否 | 是 |

适配器实现见 `Backend/src/OpenAgent.Capabilities/Rag/Adapters/`，契约见 `Backend/src/OpenAgent.Contracts/Models/IRagAdapter.cs`。

## 接入边界

- `IRagService` 面向 `IEnumerable<IRagAdapter>` 解析可用后端，按 `IRagRegistry` 的实例配置与 ACL 过滤可见范围。
- HTTP 访问统一走 `IHttpClientFactory`；后端地址来自租户级 RAG 实例配置，不在代码中固定。
- Qdrant 索引请求中向量字段使用空数组占位，实际使用需调用嵌入模型；RagFlow 不支持索引。

## Source

- Capabilities: `Backend/src/OpenAgent.Capabilities/Rag/RagCapabilitySource.cs`, `Backend/src/OpenAgent.Capabilities/Rag/RagService.cs`, `Backend/src/OpenAgent.Capabilities/Rag/Adapters/`
- Contracts: `Backend/src/OpenAgent.Contracts/Models/IRagAdapter.cs`
