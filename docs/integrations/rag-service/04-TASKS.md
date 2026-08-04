# RAG Service — 任务清单

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
