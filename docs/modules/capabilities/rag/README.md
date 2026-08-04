
## Feature


## 用户故事

作为长对话场景的用户，我希望 Agent 能检索内部知识库中的相关信息来回答问题，以便获得准确的、基于事实的回答。

## 概述

RAG（Retrieval-Augmented Generation）在 Agent.Core 中作为模型可调用的工具参与执行，而不是在所有请求开始前强制预检索。RAG 是否被调用由模型和工具链路共同决定。

## 核心能力

| 能力 | 说明 |
|------|------|
| 文档索引 | 将内容索引到外部 RAG 系统 |
| 语义检索 | 从外部 RAG 系统检索相关文档 |
| 多实例支持 | 同时配置和使用多个 RAG 实例 |
| 适配器扩展 | 通过 IRagAdapter 支持不同 RAG 产品 |
| 权限过滤 | 基于用户上下文的 ACL 过滤 |

## 已实现适配器

| 适配器 | 类型标识 | 说明 |
|--------|---------|------|
| QdrantAdapter | `qdrant` | Qdrant 向量数据库 |
| RagFlowAdapter | `ragflow` | RagFlow 知识库平台 |

## 与工具体系的关系

RAG 通过 RagSearchTool（实现 ISkillExecutor）接入工具调用体系：
- 工具名称：`search_knowledge_base`
- 由 Service 在工具收集阶段根据 `config.Rag.Enabled` 决定是否纳入

## 当前状态

**已实现** — 多实例检索、适配器扩展、权限过滤均已落地。

## 当前限制

- Qdrant 索引请求中向量字段使用空数组 `float[0]`，实际使用时需要调用嵌入模型
- RagFlowAdapter 不支持索引操作（BuildIndexRequest 返回 null）
- 适配器响应解析使用同步 `.GetAwaiter().GetResult()`
- 无检索结果缓存

## Architecture


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

## Data Models


## SearchResult

检索结果模型。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Content` | `string` | 文档内容 |
| `Metadata` | `Dictionary<string, object>` | 元数据 |
| `RelevanceScore` | `double` | 相关性分数 |
| `SourceId` | `string` | 来源 ID |
| `RagInstanceId` | `string?` | RAG 实例 ID |

## RagConfig

RAG 配置容器。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Enabled` | `bool` | 是否启用 RAG，默认 false |
| `EnabledRagInstanceIds` | `List<string>` | 启用的实例 ID 列表 |
| `Instances` | `List<RagInstanceConfig>` | 内联实例配置列表 |

## RagInstanceConfig

RAG 实例配置。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 实例标识 |
| `Name` | `string` | 实例名称 |
| `Enabled` | `bool` | 是否启用，默认 true |
| `Type` | `string` | 适配器类型（如 "ragflow"、"qdrant"），默认 "ragflow" |
| `CollectionName` | `string` | 集合名称，默认 "default" |
| `ApiEndpoint` | `string` | 外部服务地址 |
| `ApiKey` | `string` | 认证密钥 |
| `AdapterConfig` | `Dictionary<string, string>?` | 适配器专用配置 |
| `AllowedUserIds` | `List<string>` | ACL |
| `AllowedGroups` | `List<string>` | ACL |
| `AllowedTenantIds` | `List<string>` | ACL |
| `AllowedRoles` | `List<string>` | ACL |

## RagAdapterType

适配器类型常量。

| 常量 | 值 | 说明 |
|------|-----|------|
| `RagFlow` | `"ragflow"` | RagFlow 知识库 |
| `Qdrant` | `"qdrant"` | Qdrant 向量数据库 |

## QdrantAdapter 内部模型

### QdrantSearchResponse（private）

| 属性 | 类型 | 说明 |
|------|------|------|
| `Result` | `List<QdrantPoint>?` | 搜索结果列表 |

### QdrantPoint（private）

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `object?` | 点 ID |
| `Score` | `double?` | 相似度分数 |
| `Payload` | `Dictionary<string, object>?` | 负载数据 |

## RagFlowAdapter 内部模型

### RetrievalResponse（public sealed）

| 属性 | 类型 | 说明 |
|------|------|------|
| `Code` | `int` | 响应码 |
| `Data` | `RetrievalData?` | 检索数据 |
| `Message` | `string?` | 响应消息 |

### RetrievalData（public sealed）

| 属性 | 类型 | 说明 |
|------|------|------|
| `Chunks` | `List<RetrievalChunk>?` | 分块列表 |

### RetrievalChunk（public sealed）

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string?` | 分块 ID |
| `Content` | `string?` | 分块内容 |
| `DocumentName` | `string?` | 文档名称 |
| `DocName` | `string?` | 文档名称（别名） |
| `Source` | `string?` | 来源 |
| `Similarity` | `double` | 相似度分数 |

## API


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

## Tests


## 测试策略

RAG 检索增强的测试围绕配置解析、多实例检索、适配器行为和降级策略展开。

## 单元测试

### RagService 配置解析

| 测试场景 | 验证点 |
|----------|--------|
| overrideConfig 非空 | 直接使用 overrideConfig |
| overrideConfig 为空 | 根据 agentId 获取 AgentConfig.Rag |
| Instances 非空 | 使用内联实例配置 |
| Instances 为空 + EnabledRagInstanceIds 非空 | 从 IRagRegistry 筛选 |
| Enabled=false 过滤 | 不包含禁用实例 |
| ACL 过滤 | 无权限实例被排除 |

### RagService 检索

| 测试场景 | 验证点 |
|----------|--------|
| 无可用配置 | 返回空列表 |
| 单实例检索 | 正确调用适配器并返回结果 |
| 多实例检索 | 合并结果，按 RelevanceScore 降序 |
| 单实例异常 | 不中断其他实例，Error 日志 |
| ApiEndpoint 为空 | 跳过该实例，Warning 日志 |
| 适配器未找到 | 返回空列表，Error 日志 |

### RagService 索引

| 测试场景 | 验证点 |
|----------|--------|
| 无可用配置 | 跳过索引，Debug 日志 |
| 指定 ragInstanceId | 仅索引目标实例 |
| 适配器不支持索引 | 跳过索引，Debug 日志 |
| ApiEndpoint 为空 | 跳过索引，Warning 日志 |
| HTTP 请求失败 | Error 日志，不抛出 |

### RagSearchTool

| 测试场景 | 验证点 |
|----------|--------|
| 正常检索 | 返回编号列表格式 |
| query 为空 | 返回 "Error: Query parameter is required" |
| 检索异常 | 返回 "Error searching knowledge base: ..." |
| 无结果 | 返回 "No relevant information found in knowledge base." |
| limit 参数 | 默认 3，可配置 1-10 |

### QdrantAdapter

| 测试场景 | 验证点 |
|----------|--------|
| CanHandle Type 匹配 | config.Type="qdrant" 时返回 true |
| CanHandle Endpoint 匹配 | ApiEndpoint 包含 "qdrant" 时返回 true |
| BuildSearchRequest | 正确构建 POST 请求，含 api-key Header |
| ParseSearchResponse | 正确解析 Result 数组 |
| BuildIndexRequest | 返回含空向量的索引请求 |

### RagFlowAdapter

| 测试场景 | 验证点 |
|----------|--------|
| CanHandle Type 匹配 | config.Type="ragflow" 时返回 true |
| CanHandle Endpoint 匹配 | ApiEndpoint 包含 "ragflow" 时返回 true |
| BuildSearchRequest | 正确构建 POST 请求，含 Bearer Header |
| ParseSearchResponse | 正确解析 Data.Chunks |
| BuildIndexRequest | 返回 null（不支持索引） |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整检索周期 | 配置解析 → 适配器选择 → HTTP 请求 → 结果合并 |
| 多实例并行检索 | 所有实例结果合并排序 |
| 工具调用集成 | search_knowledge_base 工具被 Service 正确注册和路由 |

## 验收口径

- [ ] RAG 工具在 config.Rag.Enabled=true 时出现在工具集合
- [ ] 多实例检索结果正确合并排序
- [ ] 单实例失败不阻断其他实例
- [ ] ACL 过滤正确执行
- [ ] 适配器 CanHandle 匹配逻辑正确

## Conventions


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
