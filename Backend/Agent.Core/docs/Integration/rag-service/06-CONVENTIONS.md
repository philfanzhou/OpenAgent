# RAG Service — 约定

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
