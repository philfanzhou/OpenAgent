# 文件资产与对象存储

文件资产是独立于会话的用户资源。数据库保存文件归属、状态和会话引用；S3 兼容对象存储只保存原始字节。会话消息只记录 `FileId`、文件名、MIME 和大小，不保存字节或对象键。

## 数据流

```text
POST /files (multipart)
  -> SQLite FileAssets (Pending)
  -> S3 / MinIO object
  -> SQLite FileAssets (Ready)
  -> fileId

POST /chat/stream { fileIds }
  -> FileAssetService reads Ready assets
  -> AgentRequest.Attachments for this execution only
  -> ConversationFileReferences + message metadata
```

`Backend/src/OpenAgent.Core/Files/FileAssetService.cs` 是上传、读取和会话引用的统一入口。`Backend/src/OpenAgent.Engine.Host/Files/S3FileObjectStore.cs` 是 S3/MinIO 适配器；Core 和 Skill 不直接接触 bucket、凭据或 object key。

## 配置

文件资产默认关闭。启用时必须配置 SQLite 元数据数据库和 S3 兼容对象存储：

```json
{
  "FileAssets": {
    "Enabled": true,
    "MetadataConnectionString": "Data Source=/var/lib/openagent/files.db",
    "ObjectStorage": {
      "BucketName": "openagent-files",
      "KeyPrefix": "files",
      "Region": "us-east-1",
      "ServiceUrl": "http://minio:9000",
      "ForcePathStyle": true
    }
  }
}
```

静态访问凭据使用环境变量 `FileAssets__ObjectStorage__AccessKey` 与 `FileAssets__ObjectStorage__SecretKey` 配置；两项必须一起提供。省略两项时 AWS SDK 使用标准凭据链。

没有对象存储时保持 `Enabled=false`：普通聊天和旧的临时 multipart 端点仍可用，独立文件上传、下载和模型文件 Function 返回依赖不可用错误，不会错误地把持久文件降级为内存数据。

## API 与预览

| 端点 | 用途 |
|------|------|
| `POST /api/v1/agent/files` | 独立上传一个文件，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取文件元数据，不返回对象键 |
| `GET /api/v1/agent/files/{fileId}/content` | 获取内容，用于图片和 Markdown 预览 |
| `GET /api/v1/agent/files/{fileId}/download` | 下载原文件 |

工作台使用认证 fetch 获取预览和下载，避免把访问令牌或对象存储地址暴露在 URL 中。Markdown 预览使用纯文本插值，不执行文件中的 HTML。

## Core Function

启用文件资产后，Core 提供 `read_file(fileId)` 与 `write_file(fileName, content, mediaType)`。读取仅允许 UTF-8 文本并受 `MaxFunctionReadBytes` 限制；写入默认创建新资产，使用当前请求的租户、用户和会话上下文。文件级访问控制尚未实现，后续权限模块应在 `IFileAssetService` 统一入口校验，而不是由 Function 或 S3 适配器各自处理。
