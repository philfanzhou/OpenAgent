# 文件资产与对象存储

文件资产独立于会话：PostgreSQL 保存资产、所有者、状态和引用，S3/MinIO 只保存原始字节。聊天请求只传递 `fileIds`；执行期由文件服务读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { fileIds } -> read ready files -> model -> message and file references
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，`S3FileObjectStore` 是对象存储适配器。未配置对象存储时文件端点返回依赖不可用；不存在旧的 multipart 聊天降级路径。

模型通过 `write_file` 或 `compress_files` 生成文件时，产物会登记为 `FileAsset`，但不会自动出现在 assistant 消息中。模型调用 `publish_files` 并传入一个或多个 `fileId` 后，选中的资产才会关联到当前 assistant 消息；这允许模型保留中间产物、批量发布 Markdown 与图片，或先压缩再发布 ZIP。前端可使用消息中的 `fileId` 调用认证下载端点；模型不应直接输出未经授权的 MinIO URL。

本地依赖由仓库根目录 `docker-compose.storage.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files` | 上传一个独立资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content` | 认证预览内容 |
| `GET /api/v1/agent/files/{fileId}/download` | 认证下载 |

权限校验通过 `FileAssetScope` 的 TenantId/OwnerUserId 边界在 `FileAssetService` 内强制执行（缺失时抛 `TenantDataIsolationException`）。
