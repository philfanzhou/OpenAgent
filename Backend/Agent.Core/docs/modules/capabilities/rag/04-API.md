# API: RAG 检索增强

## IRagService

RAG 检索增强服务接口。

| 方法 | 说明 |
|------|------|
| `IndexDocumentAsync(content, metadata?, ragInstanceId?, ct)` | 索引文档到外部 RAG 系统 |
| `SearchAsync(query, limit, overrideConfig?, ct)` | 简化检索，返回内容字符串列表 |
| `SearchDetailedAsync(query, limit, overrideConfig?, ct)` | 详细检索，返回 SearchResult 列表 |

实现类：`RagService`（internal）

## IRagAdapter

RAG 适配器接口，用于对接不同 RAG 产品。

| 成员 | 说明 |
|------|------|
| `AdapterName` | 适配器名称标识 |
| `CanHandle(config)` | 判断是否能处理该配置 |
| `BuildSearchRequest(config, query, limit, filters)` | 构建搜索 HTTP 请求 |
| `ParseSearchResponse(config, response)` | 解析搜索 HTTP 响应 |
| `BuildIndexRequest(config, content, metadata)` | 构建索引 HTTP 请求，返回 null 表示不支持索引 |

实现类：`QdrantAdapter`、`RagFlowAdapter`（均为 internal）

## IRagRegistry

RAG 实例注册表接口。

| 方法 | 说明 |
|------|------|
| `GetAllInstances()` | 获取所有已注册的 RAG 实例配置 |
| `GetInstance(id)` | 按 ID 获取实例配置 |
| `Register(instance)` | 注册 RAG 实例配置 |

实现类：`RagRegistry`（internal）

## ISkillExecutor

RagSearchTool 实现此接口以接入工具调用体系。

| 成员 | 说明 |
|------|------|
| `Name` | 固定为 `search_knowledge_base` |
| `Description` | 工具描述 |
| `ParametersJsonSchema` | 参数 Schema |
| `ExecuteAsync(toolName, arguments, userContext?, ct)` | 执行检索 |

## 调用方使用模式

### 检索

```csharp
var results = await ragService.SearchAsync(query, limit, ct);
// 或详细检索
var detailedResults = await ragService.SearchDetailedAsync(query, limit, ct);
```

### 索引

```csharp
await ragService.IndexDocumentAsync(content, metadata, ragInstanceId, ct);
```

### 通过工具调用

```csharp
// 由 Service 自动注册为 search_knowledge_base 工具
// 模型在推理时决定是否调用
```
