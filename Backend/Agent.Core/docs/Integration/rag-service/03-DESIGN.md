# RAG Service — 设计文档

## IRagAdapter 适配器模式

Agent.Core 采用 **适配器模式** 将不同 RAG 后端统一到 `IRagService` 接口下：

```
IRagService (RagService)
  ├── IEnumerable<IRagAdapter> (DI 注入所有适配器)
  │     ├── QdrantAdapter : IRagAdapter
  │     └── RagFlowAdapter : IRagAdapter
  ├── IAgentConfigProvider → 获取 RagConfig
  ├── IRagRegistry → 获取全局 RAG 实例
  └── IHttpClientFactory → 创建 HTTP 客户端
```

## RagService 检索流程

```
RagService.SearchDetailedAsync(query, limit, overrideConfig)
  ├── ResolveUserContext() → 从 HttpContext 获取用户上下文
  ├── GetAllowedRagConfigsAsync(userContext, overrideConfig)
  │     ├── overrideConfig 不为空 → 使用传入配置
  │     ├── overrideConfig 为空
  │     │     ├── IAgentConfigProvider.GetConfigAsync(agentId) → AgentConfig
  │     │     └── AgentConfig.Rag → RagConfig
  │     ├── RagConfig.Instances 不为空 → 使用内联实例
  │     ├── RagConfig.EnabledRagInstanceIds 不为空 → 从 IRagRegistry 查找
  │     ├── 过滤 c.Enabled == true
  │     └── 过滤 IsAllowedForUser(config, userContext)
  └── 遍历每个允许的实例
        ├── GetAdapter(config) → _adapters.FirstOrDefault(a => a.CanHandle(config))
        ├── BuildAclFilters(userContext) → { tenant_id: ... }
        ├── adapter.BuildSearchRequest(config, query, limit, filters)
        ├── httpClient.SendAsync(request)
        └── adapter.ParseSearchResponse(config, response)
```

## RagService 索引流程

```
RagService.IndexDocumentAsync(content, metadata, ragInstanceId)
  ├── ResolveUserContext()
  ├── GetAllowedRagConfigsAsync(userContext, null)
  ├── 若指定 ragInstanceId → 过滤到目标实例
  ├── EnrichMetadata(metadata, userContext)
  │     ├── "indexed_at" → DateTime.UtcNow
  │     ├── "indexed_by" → userContext.UserId ?? "Agent.Engine"
  │     └── "tenant_id" → userContext.TenantId ?? "default"
  └── 遍历每个允许的实例
        ├── GetAdapter(config)
        ├── adapter.BuildIndexRequest(config, content, enrichedMetadata)
        │     ├── 返回 null → 适配器不支持索引（如 RagFlow）
        │     └── 返回 HttpRequestMessage → 发送请求
        └── httpClient.SendAsync(request)
```

## QdrantAdapter 设计

```
QdrantAdapter
  ├── AdapterName = "qdrant"
  ├── CanHandle(config) → config.Type == "qdrant" || endpoint 包含 "qdrant"
  ├── BuildSearchRequest()
  │     ├── POST {ApiEndpoint}/collections/{CollectionName}/points/search
  │     ├── Body: { limit, with_payload: true, with_vector: false, filter }
  │     └── Header: api-key (若配置)
  ├── ParseSearchResponse()
  │     └── 解析 QdrantSearchResponse.Result[]
  │           → SearchResult { Content, Metadata, RelevanceScore, SourceId, RagInstanceId }
  └── BuildIndexRequest()
        ├── POST {ApiEndpoint}/collections/{CollectionName}/points
        ├── Body: { points: [{ payload, vector: [] }] }
        └── Header: api-key (若配置)
```

注意：Qdrant 索引需要向量，当前实现使用空向量占位，实际使用时需要调用嵌入模型。

## RagFlowAdapter 设计

```
RagFlowAdapter
  ├── AdapterName = "ragflow"
  ├── CanHandle(config) → config.Type == "ragflow" || endpoint 包含 "ragflow"
  ├── BuildSearchRequest()
  │     ├── POST {ApiEndpoint}/api/v1/retrieval (或自定义 search_endpoint)
  │     ├── Body: { dataset_ids, question, top_k }
  │     └── Header: Authorization: Bearer {ApiKey}
  ├── ParseSearchResponse()
  │     └── 解析 RetrievalResponse.Data.Chunks[]
  │           → SearchResult { Content, SourceId, RagInstanceId, RelevanceScore }
  └── BuildIndexRequest() → 返回 null（RagFlow 不支持通过 API 索引）
```

## RagSearchTool 集成

RAG 检索通过 `RagSearchTool` 暴露为 Agent 可调用的工具：

```
Agent → ToolCall("search_knowledge_base") → Service.ExecuteToolAsync()
  → _ragSearchTool.ExecuteAsync() → IRagService.SearchAsync/SearchDetailedAsync
  → IRagAdapter → RAG Backend
```

- 工具名：`search_knowledge_base`
- 仅在 `AgentConfig.Rag.Enabled == true` 且 `_ragSearchTool != null` 时注册
- 检索结果注入对话上下文，供 LLM 生成回答

## ACL 过滤器构建

```
BuildAclFilters(userContext)
  └── { "tenant_id": userContext.TenantId ?? "default" }
```

当前仅按 `tenant_id` 过滤，传递给适配器的 `BuildSearchRequest` 方法。
