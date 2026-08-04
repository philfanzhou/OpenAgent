# Architecture: RAG 检索增强

## 配置解析流程

RagService.GetAllowedRagConfigsAsync：

1. 若有 overrideConfig，直接使用
2. 否则根据 agentId 获取 AgentConfig，取其 Rag 属性
3. 若 RagConfig.Instances 非空，使用内联实例配置
4. 否则从 IRagRegistry 中按 EnabledRagInstanceIds 筛选
5. 过滤掉 Enabled=false 的实例
6. 过滤掉用户无权访问的实例（IsAllowedForUser）

## 检索流程

RagService.SearchDetailedAsync：

1. 解析用户上下文（优先从 `IAgentRequestContext.UserContext`，回退到 `HttpContext.Items["AgentUserContext"]`）
2. 获取允许的 RAG 配置列表
3. 无可用配置 → 返回空列表
4. 遍历每个配置：
   - 获取适配器（IRagAdapter.CanHandle 匹配）
   - 构建 ACL 过滤器（tenant_id）
   - 构建搜索请求
   - 发送 HTTP 请求
   - 解析响应为 SearchResult 列表
   - 单实例异常不中断，记录 Error 日志继续
5. 合并所有结果，按 RelevanceScore 降序排序
6. 取前 limit 条返回

## 索引流程

RagService.IndexDocumentAsync：

1. 解析用户上下文
2. 获取允许的 RAG 配置列表
3. 若指定 ragInstanceId，进一步筛选
4. 丰富元数据（添加 indexed_at、indexed_by、tenant_id）
5. 遍历每个配置：
   - 获取适配器
   - 构建索引请求（适配器返回 null 表示不支持索引）
   - 发送 HTTP 请求

## 适配器选择

GetAdapter 逻辑：遍历所有注册的 IRagAdapter，返回第一个 CanHandle(config) 为 true 的适配器。

### QdrantAdapter.CanHandle

- config.Type 等于 "qdrant"（忽略大小写）
- 或 config.ApiEndpoint 包含 "qdrant"（忽略大小写）

### RagFlowAdapter.CanHandle

- config.Type 等于 "ragflow"（忽略大小写）
- 或 config.ApiEndpoint 包含 "ragflow"（忽略大小写）

## RagSearchTool 行为

1. 从 arguments 提取 query 和 limit（默认 3）
2. query 为空 → 返回错误字符串
3. 调用 IRagService.SearchAsync（内部调用 SearchDetailedAsync 并取 Content 列表）
4. 无结果 → 返回 "No relevant information found in knowledge base."
5. 有结果 → 格式化为编号列表返回

## ACL 过滤器构建

BuildAclFilters：

- 若 userContext.TenantId 非空 → `filters["tenant_id"] = userContext.TenantId`
- 否则 → `filters["tenant_id"] = "default"`

## 用户上下文解析

ResolveUserContext：优先从已填充的 `IAgentRequestContext.UserContext` 获取（主机中间件统一解析）；
若 request context 未填充，回退到 `IHttpContextAccessor.HttpContext.Items["AgentUserContext"]`。

ResolveAgentId：
1. 优先从调用上下文 `context["AgentId"]` 获取
2. 其次从 `IAgentRequestContext.AgentId` 获取
3. 再次从 HttpContext.Request.Headers["X-Agent-Id"] 获取
4. 再次从 HttpContext.Items["AgentId"] 获取
5. 兜底返回 "default"

## 错误处理

### 错误码

与 RAG 相关的 AgentErrorCode：

| 错误码 | 值 | 说明 |
|--------|-----|------|
| `RagRetrievalFailed` | 3001 | RAG 检索失败 |
| `RagIndexNotFound` | 3002 | RAG 索引未找到 |
| `RagPermissionDenied` | 3003 | RAG 权限不足 |

### RagService 检索错误

| 场景 | 行为 |
|------|------|
| 无可用 RAG 配置 | 返回空列表，Debug 日志 |
| 适配器未找到 | 返回空列表，Error 日志 |
| ApiEndpoint 为空 | 跳过该实例，Warning 日志 |
| HTTP 请求失败 | 跳过该实例，Error 日志，不中断其他实例 |

### RagService 索引错误

| 场景 | 行为 |
|------|------|
| 无可用 RAG 配置 | 跳过索引，Debug 日志 |
| 目标实例不可访问 | 跳过索引，Warning 日志 |
| 适配器不支持索引 | 跳过索引，Debug 日志 |
| ApiEndpoint 为空 | 跳过索引，Warning 日志 |
| HTTP 请求失败 | Error 日志，不抛出 |

### RagSearchTool 错误返回

| 场景 | 返回值 |
|------|--------|
| query 参数为空 | `"Error: Query parameter is required"` |
| 检索异常 | `"Error searching knowledge base: {ex.Message}"` |
| 无结果 | `"No relevant information found in knowledge base."` |

### 适配器特殊行为

- **QdrantAdapter**：索引请求中向量字段使用空数组 `float[0]`；ParseSearchResponse 使用同步 `.GetAwaiter().GetResult()` 读取 HTTP 响应
- **RagFlowAdapter**：BuildIndexRequest 返回 null（不支持索引）；ParseSearchResponse 使用同步 `.GetAwaiter().GetResult()` 读取 HTTP 响应

### 降级策略

- 单实例检索失败不阻断其他实例
- 无可用实例时返回空结果继续执行
- 检索结果为空时，RagSearchTool 返回提示信息而非错误

### 排障指南

| 现象 | 可能原因 | 排查方向 |
|------|---------|---------|
| RAG 工具未出现 | 配置未启用 | 检查 RagConfig.Enabled |
| RAG 工具未出现 | RagSearchTool 未注入 | 检查 DI 注册 |
| 检索返回空结果 | 无可用实例 | 检查实例 Enabled 和 ACL |
| 检索返回空结果 | ApiEndpoint 为空 | 检查 RagInstanceConfig.ApiEndpoint |
| 检索返回空结果 | 适配器未匹配 | 检查 Type 字段和 ApiEndpoint |
| 索引未生效 | 适配器不支持 | RagFlowAdapter 不支持索引 |
