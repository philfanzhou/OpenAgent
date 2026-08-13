# 文件资产与对象存储

文件资产在上传时绑定租户、用户和会话。PostgreSQL 保存资产归属、状态和引用，S3/MinIO 只保存原始字节。聊天请求传递 `conversationId` 与 `fileIds`；执行期由文件服务按当前请求范围校验后读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files?conversationId=... -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { conversationId, fileIds } -> validate scope -> read ready files -> model
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，读取时必须同时匹配 `TenantId`、`OwnerUserId` 和 `ConversationId`。`S3FileObjectStore` 使用 `tenants/{tenantHash}/users/{userHash}/conversations/{conversationHash}/{fileId}` 的对象键结构；哈希分区避免在对象键中暴露原始身份。匿名请求使用用户上下文提供的 `anonymous` 身份，因此同样拥有用户分区，并继续由会话分区隔离。未配置对象存储时文件端点返回依赖不可用。

Web 客户端在上传前生成草稿 `conversationId`，首轮聊天请求体沿用该值。经 Router 访问时同时发送 `X-New-Conversation: true`，让 Router 仍执行首轮 Agent 选择，而 Engine 使用草稿 ID 建立会话和校验文件范围。

本地依赖由仓库根目录 `docker-compose.storage.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files?conversationId={id}` | 上传当前会话资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}?conversationId={id}` | 读取当前会话的资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content?conversationId={id}` | 预览当前会话内容 |
| `GET /api/v1/agent/files/{fileId}/download?conversationId={id}` | 下载当前会话文件 |

所有端点和模型文件工具均通过 `IFileAssetService` 执行同一范围校验。其他租户、用户或会话的 `fileId` 对当前请求不可见，范围不匹配时不会访问对象存储。
