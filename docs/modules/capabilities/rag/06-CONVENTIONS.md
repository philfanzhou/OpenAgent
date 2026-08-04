# Conventions: RAG 检索增强

## 配置约定

- RAG 配置分两层：可用实例层（RagInstanceConfig）和 Agent 启用层（RagConfig）
- 系统有实例不等于当前 Agent 会使用它
- Agent 启用和实例可见性是两个独立问题
- RagConfig.Enabled=false 时，RAG 工具不会出现在工具集合中

## 适配器约定

- 适配器通过 CanHandle 方法判断是否能处理某个配置
- 判断依据：config.Type 精确匹配 或 config.ApiEndpoint 包含关键字
- 适配器名称使用 RagAdapterType 常量（"ragflow"、"qdrant"）
- 新增适配器需实现 IRagAdapter 并注册到 DI

## 工具集成约定

- RAG 工具固定名称为 `search_knowledge_base`
- 工具描述强调"内部知识库"，与外部 MCP 工具区分
- 参数 Schema 包含 query（必填）和 limit（可选，1-10，默认 3）
- 检索结果格式化为编号列表

## 权限约定

- ACL 列表（AllowedUserIds、AllowedGroups、AllowedTenantIds、AllowedRoles）均为空时允许所有用户
- ACL 非空且 userContext 为 null 时拒绝访问
- 任一 ACL 维度匹配即允许（OR 语义）
- 检索请求自动携带 tenant_id 过滤器

## 元数据约定

索引时自动丰富以下元数据：
- `indexed_at`：UTC 时间（ISO 8601 格式，"O" 格式字符串）
- `indexed_by`：用户 ID 或 "Agent.Engine"
- `tenant_id`：用户租户 ID 或 "default"（不覆盖已有值）

## 降级约定

- 外部 RAG 服务不可用时返回空结果，不抛出异常
- 实例配置缺失或无效时跳过该实例
- 单实例失败不影响其他实例的检索
- 无可用实例时明确降级返回空结果

## 适配器实现约定

- BuildSearchRequest 返回完整的 HttpRequestMessage
- BuildIndexRequest 返回 null 表示不支持索引操作
- ParseSearchResponse 将 HTTP 响应转为 List<SearchResult>
- 认证信息通过 HTTP Header 传递（ApiKey 字段）
- Qdrant 使用 `api-key` Header
- RagFlow 使用 `Authorization: Bearer {apiKey}` Header
