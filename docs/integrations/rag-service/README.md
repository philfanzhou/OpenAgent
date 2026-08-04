
## Feature


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

## Specification


## 接口契约

### IRagService

```csharp
// src/Core/Abstract/IRagService.cs
public interface IRagService
{
    Task IndexDocumentAsync(string content, Dictionary<string, object>? metadata = null,
        string? ragInstanceId = null, CancellationToken ct = default);
    Task<List<string>> SearchAsync(string query, int limit = 3,
        RagConfig? overrideConfig = null, CancellationToken ct = default);
    Task<List<SearchResult>> SearchDetailedAsync(string query, int limit = 3,
        RagConfig? overrideConfig = null, CancellationToken ct = default);
}
```

### IRagAdapter

```csharp
// Agent.Contracts/Models/IRagAdapter.cs
public interface IRagAdapter
{
    string AdapterName { get; }
    bool CanHandle(RagInstanceConfig config);
    HttpRequestMessage BuildSearchRequest(RagInstanceConfig config, string query,
        int limit, Dictionary<string, object>? filters);
    List<SearchResult> ParseSearchResponse(RagInstanceConfig config,
        HttpResponseMessage response);
    HttpRequestMessage? BuildIndexRequest(RagInstanceConfig config, string content,
        Dictionary<string, object>? metadata);
}
```

### RagInstanceConfig

```csharp
// Agent.Contracts/Configuration/AgentConfig.cs
public class RagInstanceConfig
{
    public string Id { get; set; }                              // 实例唯一标识
    public string Name { get; set; }                            // 显示名称
    public bool Enabled { get; set; }                           // 是否启用（默认 true）
    public string Type { get; set; }                            // 后端类型（"qdrant"/"ragflow"）
    public string CollectionName { get; set; }                  // 集合名称
    public string ApiEndpoint { get; set; }                     // API 端点
    public string ApiKey { get; set; }                          // API 密钥
    public Dictionary<string, string>? AdapterConfig { get; set; }  // 适配器扩展配置
    public List<string> AllowedUserIds { get; set; }            // ACL: 允许的用户
    public List<string> AllowedGroups { get; set; }             // ACL: 允许的组
    public List<string> AllowedTenantIds { get; set; }          // ACL: 允许的租户
    public List<string> AllowedRoles { get; set; }              // ACL: 允许的角色
}
```

### RagConfig

```csharp
public class RagConfig
{
    public bool Enabled { get; set; }                           // 是否启用 RAG
    public List<string> EnabledRagInstanceIds { get; set; }     // 启用的实例 ID
    public List<RagInstanceConfig> Instances { get; set; }      // 实例配置列表
}
```

## 适配器路由规则

`RagService` 通过 `IRagAdapter.CanHandle(config)` 路由到正确的适配器：

- **QdrantAdapter**：`config.Type == "qdrant"` 或 `config.ApiEndpoint` 包含 "qdrant"
- **RagFlowAdapter**：`config.Type == "ragflow"` 或 `config.ApiEndpoint` 包含 "ragflow"

## ACL 权限控制

每个 `RagInstanceConfig` 支持 4 种 ACL 规则：

| ACL 字段 | 说明 |
|----------|------|
| `AllowedUserIds` | 允许访问的用户 ID 列表 |
| `AllowedGroups` | 允许访问的用户组列表 |
| `AllowedTenantIds` | 允许访问的租户 ID 列表 |
| `AllowedRoles` | 允许访问的角色列表 |

- 所有 ACL 列表为空时，允许所有人访问
- 任一 ACL 匹配即允许访问（OR 逻辑）
- 用户上下文为 null 且有 ACL 限制时，拒绝访问

## QdrantAdapter 扩展配置

| 键 | 说明 | 默认值 |
|----|------|--------|
| `query_field` | 查询字段名 | `"content"` |

## RagFlowAdapter 扩展配置

| 键 | 说明 | 默认值 |
|----|------|--------|
| `search_endpoint` | 搜索端点路径 | `"/api/v1/retrieval"` |
| `knowledge_base_id` | 知识库 ID | `config.CollectionName` |
| `knowledge_id` | 知识库 ID（别名） | `config.CollectionName` |

## Design


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

## Tasks


## 已完成

- [x] `IRagService` 接口定义（IndexDocumentAsync/SearchAsync/SearchDetailedAsync）
- [x] `IRagAdapter` 适配器接口定义（AdapterName/CanHandle/BuildSearchRequest/ParseSearchResponse/BuildIndexRequest）
- [x] `RagService` 实现（含 ACL 权限控制、适配器路由、元数据增强）
- [x] `QdrantAdapter` 适配器实现（搜索 + 索引）
- [x] `RagFlowAdapter` 适配器实现（仅搜索，不支持索引）
- [x] `RagInstanceConfig` ACL 权限控制模型
- [x] `RagSearchTool` 工具集成（`search_knowledge_base`）
- [x] `RagConfig` 支持 `Instances` 和 `EnabledRagInstanceIds` 两种配置方式
- [x] 适配器扩展配置（`AdapterConfig` 字典）

## 待办

- [ ] Qdrant 索引时集成嵌入模型（当前使用空向量占位）
- [ ] 补充更多 RAG 后端适配器（如 Milvus、Weaviate）
- [ ] 检索结果缓存机制
- [ ] ACL 过滤器扩展（除 tenant_id 外的更多过滤维度）

## Tests


## 单元测试

### RagService

- `SearchAsync` 返回内容字符串列表
- `SearchDetailedAsync` 返回 `SearchResult` 列表（含 RelevanceScore）
- `SearchDetailedAsync` 无启用 RAG 配置时返回空列表
- `SearchDetailedAsync` 使用 overrideConfig 跳过 AgentConfig
- `SearchDetailedAsync` 多实例结果按 RelevanceScore 降序排列，取 top N
- `IndexDocumentAsync` 无启用 RAG 配置时跳过
- `IndexDocumentAsync` 指定 ragInstanceId 时仅索引目标实例
- `IndexDocumentAsync` ragInstanceId 不存在时跳过
- `EnrichMetadata` 正确添加 indexed_at/indexed_by/tenant_id
- `IsAllowedForUser` ACL 全空时允许访问
- `IsAllowedForUser` 用户 ID 匹配时允许
- `IsAllowedForUser` 用户组匹配时允许
- `IsAllowedForUser` 租户匹配时允许
- `IsAllowedForUser` 角色匹配时允许
- `IsAllowedForUser` 无匹配时拒绝
- `IsAllowedForUser` 用户上下文为 null 且有 ACL 时拒绝

### QdrantAdapter

- `CanHandle` Type 为 "qdrant" 时返回 true
- `CanHandle` ApiEndpoint 包含 "qdrant" 时返回 true
- `BuildSearchRequest` 正确构建搜索 URL 和请求体
- `BuildSearchRequest` 配置 ApiKey 时添加 api-key Header
- `ParseSearchResponse` 正确解析 Qdrant 搜索结果
- `BuildIndexRequest` 正确构建索引 URL 和请求体

### RagFlowAdapter

- `CanHandle` Type 为 "ragflow" 时返回 true
- `CanHandle` ApiEndpoint 包含 "ragflow" 时返回 true
- `BuildSearchRequest` 正确构建检索 URL 和请求体
- `BuildSearchRequest` 配置 ApiKey 时添加 Authorization Header
- `BuildSearchRequest` 自定义 search_endpoint 时正确拼接 URL
- `ParseSearchResponse` 正确解析 RagFlow 检索结果
- `BuildIndexRequest` 返回 null（不支持索引）

## 集成测试

- 端到端检索（需 Mock RAG 后端）
- 多适配器路由正确性
- ACL 权限控制端到端验证

## Conventions


## 适配器模式

- 所有 RAG 后端必须实现 `IRagAdapter` 接口
- 适配器注册到 DI 容器，由 `RagService` 通过 `IEnumerable<IRagAdapter>` 注入
- 适配器路由：`_adapters.FirstOrDefault(a => a.CanHandle(config))`
- 新增后端只需实现 `IRagAdapter`，无需修改核心逻辑

## 失败语义

- 检索失败时返回空结果集，不抛出异常
- 单个实例检索失败不影响其他实例（catch 后继续）
- Agent 收到空结果后可决定：重试、换用其他知识源、或直接回答
- 适配器内部错误记录 Warning 日志，不向上传播
- 索引失败记录 Error 日志，不阻塞主流程

## 结果格式

- 检索结果统一为 `SearchResult` 列表
- 每条结果包含：Content / SourceId / RelevanceScore / Metadata / RagInstanceId
- 多实例结果合并后按 `RelevanceScore` 降序排列，取 top `limit` 条

## ACL 约定

- 4 种 ACL 规则（AllowedUserIds/Groups/TenantIds/Roles）为 OR 逻辑
- 所有 ACL 列表为空时，允许所有人访问（开放模式）
- 用户上下文为 null 且有 ACL 限制时，拒绝访问
- ACL 检查在配置获取阶段完成，不在适配器层面

## 配置约定

- `RagConfig` 支持两种配置方式：
  - `Instances`：直接内联实例配置
  - `EnabledRagInstanceIds`：引用 `IRagRegistry` 中的全局实例
- `RagInstanceConfig.Type` 用于适配器路由（"qdrant" / "ragflow"）
- `RagInstanceConfig.AdapterConfig` 为适配器扩展配置字典

## 元数据增强

- 索引时自动添加 `indexed_at`、`indexed_by`、`tenant_id` 元数据
- 不覆盖已有的 `tenant_id` 字段

## 工具集成约定

- 工具名固定为 `search_knowledge_base`
- 仅在 `AgentConfig.Rag.Enabled == true` 且 `RagSearchTool` 已注入时注册
- 工具名以 `search_knowledge_base` 调用时路由到 `_ragSearchTool`
