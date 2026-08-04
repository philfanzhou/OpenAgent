# RAG Service — 测试清单

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
