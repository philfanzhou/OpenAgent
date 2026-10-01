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

## MAF TextSearchProvider 兼容性

以当前安装的 `Microsoft.Agents.AI 1.14.0` 为基线，
`TextSearchProviderCompatibilityTests` 通过真实 `ChatClientAgent` 函数循环验证按需检索和结果引用格式。
原生工具参数为 `userQuestion`，不提供当前 `search_knowledge_base(query, limit)` 的单次结果数量参数。
现阶段保留平台兼容工具及 `IRagService` 的用户上下文/ACL；生产链未切换为原生 Provider。

`query` 必须包含非空白内容；未传 `limit` 默认 3，显式值必须为 1–10 的整数。
无结果提示、编号引用、错误净化和请求取消行为保持不变。
若迁移，须先解决 query/limit 协议适配、详细来源映射与工具授权，再通过统一调用策略验证；
仅替换工具名称会丢失契约，增加两层工具转换也不能减少当前维护量。

原生 API 的参考见 [TextSearchProvider 源码](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI/TextSearchProvider.cs)；
版本升级后以兼容性测试重新核对，不以滚动文档代替当前 SDK 的行为。
