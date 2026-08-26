# 文件资产与对象存储

文件资产独立于会话：PostgreSQL 保存资产、所有者、状态和引用，S3/MinIO 只保存原始字节。聊天请求只传递 `fileIds`；执行期由文件服务读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { fileIds } -> read ready files -> model -> message and file references
model -> download_file(url) -> HTTP(S) resource -> current conversation's FileAsset -> S3/MinIO
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，`S3FileObjectStore` 是对象存储适配器。未配置对象存储时文件端点返回依赖不可用；不存在旧的 multipart 聊天降级路径。

启用文件资产后，模型还可以调用 `download_file`。该函数只接收公开的 HTTP(S) 地址，下载结果使用 `FileAssetSource.Agent` 写入当前租户、用户和会话范围，并建立会话引用；工具返回 `fileId`、文件名、MIME 和长度，后续消息可直接携带该文件资产。下载器会限制响应大小、超时和重定向次数，并拒绝回环、链路本地、私有网段及多播地址。

下载器使用固定的超时和重定向上限；响应的文件名和 MIME 仍须满足现有 `AllowedExtensions` / `AllowedMediaTypes` 白名单。

本地依赖由仓库根目录 `docker-compose.storage.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files` | 上传一个独立资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content` | 认证预览内容 |
| `GET /api/v1/agent/files/{fileId}/download` | 认证下载 |

权限校验通过 `FileAssetScope` 的 TenantId/OwnerUserId 边界在 `FileAssetService` 内强制执行（缺失时抛 `TenantDataIsolationException`）。
