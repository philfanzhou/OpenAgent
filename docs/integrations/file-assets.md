# 文件资产与对象存储

文件资产独立于会话：PostgreSQL 保存资产、所有者、状态、实际 S3 `ObjectKey` 和引用，S3/MinIO 只保存原始字节。聊天请求只传递 `fileIds`；执行期由文件服务读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { fileIds } -> read ready files -> model -> message and file references
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，`S3FileObjectStore` 是对象存储适配器。未配置对象存储时文件端点返回依赖不可用；不存在旧的 multipart 聊天降级路径。

模型通过 `write_file` 或 `compress_files` 生成文件时，产物会登记为 `FileAsset`，但不会自动出现在 assistant 消息中。模型调用 `publish_files` 并传入一个或多个 `fileId` 后，选中的资产才会关联到当前 assistant 消息；这允许模型保留中间产物、批量发布 Markdown 与图片，或先压缩再发布 ZIP。消息只要带有文件引用，续接会话时都会把对应文件重新注入原消息的模型上下文（user、assistant 均适用）；模型还可以调用 `list_files` 发现当前会话引用的文件，再按需 `read_file` 或 `publish_files`。是否向用户交付仍由消息级发布引用决定。首条消息也必须沿用前端创建的 conversationId，确保上传文件引用和本次模型请求使用同一会话范围。前端可使用消息中的 `fileId` 调用认证下载端点；模型不应直接输出未经授权的 MinIO URL。

本地依赖由仓库根目录 `docker-compose.storage.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files` | 上传一个独立资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content` | 认证预览内容 |
| `GET /api/v1/agent/files/{fileId}/download` | 认证下载 |

权限校验通过 `FileAssetScope` 的 TenantId/OwnerUserId 边界在 `FileAssetService` 内强制执行（缺失时抛 `TenantDataIsolationException`）。

## MCP 跨系统传输

跨系统传输不提供独立的 HTTP 生成端点。只有大模型判断某个第三方 MCP 工具需要文件 URL 时，才调用 Agent 内部工具 `create_file_transfer_url`，并将返回的 URL 作为参数传给该 MCP 工具。普通上传、查询、预览、下载和聊天流程不会生成临时 URL。

响应示例：

```json
{
  "fileId": "c745f86af1e44857ac63d463f0bc0495",
  "objectKey": "files/tenants/{tenant-sha256}/users/{user-sha256}/c745f86af1e44857ac63d463f0bc0495.pdf",
  "url": "https://s3.example.com/openagent-files?...",
  "expiresAt": "2026-08-26T12:00:00Z"
}
```

`fileId` 是 OpenAgent 的业务资产 ID；`objectKey` 是 S3 对象的实际键，不能把二者混称为“S3 ID”。S3 对象由 bucket 与 `objectKey` 定位。`url` 是模型调用 `create_file_transfer_url` 时才生成的、有效期 15 分钟的只读签名 URL，第三方 MCP 应使用它读取文件，不应保存 S3 凭据或依赖租户/用户路径。

签名 URL 使用对象存储客户端配置的 S3 endpoint 生成；如果部署 MinIO 或其他 S3-compatible 存储，`ServiceUrl` 必须是第三方能够访问的地址，而不能是仅 Engine 容器可访问的内部地址。
