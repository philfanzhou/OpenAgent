# RAG Service — 规格说明

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
