# Data Models: RAG 检索增强

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
