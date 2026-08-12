# 文件资产与对象存储

文件资产独立于会话：PostgreSQL 保存资产、所有者、状态和引用，S3/MinIO 只保存原始字节。聊天请求只传递 `fileIds`；执行期由文件服务读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { fileIds } -> read ready files -> model -> message and file references
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，`S3FileObjectStore` 是对象存储适配器。未配置对象存储时文件端点返回依赖不可用；不存在旧的 multipart 聊天降级路径。

本地依赖由仓库根目录 `docker-compose.storage.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files` | 上传一个独立资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content` | 认证预览内容 |
| `GET /api/v1/agent/files/{fileId}/download` | 认证下载 |

权限校验将在后续 PR 中统一接入 `IFileAssetService`；当前数据模型已保留 TenantId、OwnerUserId 和会话引用边界。
