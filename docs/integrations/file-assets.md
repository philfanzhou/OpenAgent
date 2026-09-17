# 文件资产与对象存储

文件资产独立于会话：PostgreSQL 保存资产、所有者、状态、实际 S3 `ObjectKey` 和引用，S3/MinIO 只保存原始字节。聊天请求只传递 `fileIds`；执行期由文件服务读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { fileIds } -> read ready files -> model -> message and file references
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，`S3FileObjectStore` 是对象存储适配器。未配置对象存储时文件端点返回依赖不可用；不存在旧的 multipart 聊天降级路径。

LLM Profile 选择 `Multimodal` 时，聊天请求中的 `image/*` 资产会在执行期以内联图片内容发送给模型；默认每次最多 4 张、每张不超过 4 MiB，可通过 `FileAssets:MaxInlineImageCount` 和 `FileAssets:MaxInlineImageBytes` 调整。`Text` Profile、非图片文件、超限图片和对象读取失败均不会发送二进制，只保留 fileId manifest。当前未开放音频、视频等其他多模态输入。

模型通过 `write_file` 或 `compress_files` 生成文件时，产物会登记为 `FileAsset`，但不会自动出现在 assistant 消息中。模型调用 `publish_files` 并传入一个或多个 `fileId` 后，选中的资产才会关联到当前 assistant 消息；这允许模型保留中间产物、批量发布 Markdown 与图片，或先压缩再发布 ZIP。消息只要带有文件引用，续接会话时都会把对应文件重新注入原消息的模型上下文（user、assistant 均适用）；模型还可以调用 `list_files` 发现当前会话引用的文件，再按需 `read_file` 或 `publish_files`。是否向用户交付仍由消息级发布引用决定。首条消息也必须沿用前端创建的 conversationId，确保上传文件引用和本次模型请求使用同一会话范围。前端可使用消息中的 `fileId` 调用认证下载端点；模型不应直接输出未经授权的 MinIO URL。

启用文件资产后，模型还可以调用 `download_file`。该函数只接收公开的 HTTP(S) 地址，下载结果写入当前租户、用户和会话范围，并建立会话引用；工具返回 `fileId`、文件名、MIME 和长度。下载器会限制响应大小、超时和重定向次数，并拒绝回环、链路本地、私有网段及多播地址。

本地依赖由仓库根目录 `deploy/infrastructure/docker-compose.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files` | 上传一个独立资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content` | 认证预览内容 |
| `GET /api/v1/agent/files/{fileId}/download` | 认证下载 |
| `POST /api/v1/agent/files/{fileId}/share` | 创建分享链接（认证，body 指定 `mode`/`expiresInSeconds`） |
| `GET /api/v1/agent/files/shares` | 查询当前用户在该租户下的全部分享链接（认证，按创建时间倒序，含 `isActive`） |
| `DELETE /api/v1/agent/files/shares/{shareId}` | 撤销一条分享链接（认证）；删除后令牌立即失效，不存在/非本人统一 404 |
| `GET /api/v1/share/{token}` | 匿名分享下载；令牌即凭证，过期或超次数统一 404 |

权限校验通过 `FileAssetScope` 的 TenantId/OwnerUserId 边界在 `FileAssetService` 内强制执行（缺失时抛 `TenantDataIsolationException`）。

## 文件分享链接（MCP 传输 / 用户分享）

分享链接由平台自身的文件分享服务签发与核销（`FileShareService` + `openagent.file_share_links` 表），对外只暴露
`/api/v1/share/{token}` 这个不透明地址，不再生成 S3 预签名 URL，因此不会泄露对象存储 endpoint、bucket、
对象键布局或 Access Key。数据库只保存令牌的 SHA-256 哈希，与第三方 API Key 的存储约定一致；下载计数通过
条件 `UPDATE` 原子核销，多实例部署下“单次下载”仍然严格。

链接策略有两层：**消费方默认**（`audience`，未显式指定 `mode` 时生效）与**显式模式**（`mode`）。

消费方默认策略（`audience`）：

| 消费方 | 默认有效期 | 默认下载限制 |
|---|---|---|
| `mcp`（交给第三方 MCP 工具拉取） | 2 小时 | 最多 2 次 |
| `user`（交给最终用户；默认值） | 3 天 | 不限次数 |

显式模式（`mode`，指定后覆盖 audience 默认）：

| 模式 | 有效期默认值 | 下载限制 |
|---|---|---|
| `temporary` | 15 分钟 | 有效期内不限次数 |
| `singleUse` | 24 小时 | 仅 1 次，下载后立即失效 |
| `longTerm` | 30 天 | 有效期内不限次数 |

按 audience 默认生成的分享在查询列表中显示为 `Custom` 模式，实际有效期/次数以
`ExpiresAt`/`MaxDownloads` 为准。REST 创建端点（`POST /files/{fileId}/share`，body 也可带
`audience`）不指定任何参数时按 `user` 默认策略生成。

`expiresInSeconds` 可覆盖模式或消费方的默认时长，实现自定义失效日期；必须为正数，上限受
`FileAssets:Share:MaxLifetimeSeconds` 约束，而该配置本身有 **365 天硬上限**
（`FileShareOptions.MaxLifetimeLimitSeconds`，配置超过会在启动校验时失败），因此不存在永久有效的分享。
各默认时长通过 `FileAssets:Share` 配置：`TemporaryLifetimeSeconds`、`SingleUseLifetimeSeconds`、
`LongTermLifetimeSeconds`、`UserAudienceLifetimeSeconds`、`McpAudienceLifetimeSeconds`、
`McpAudienceMaxDownloads`；绝对地址基地址配置 `FileAssets:Share:PublicBaseUrl`（未配置时 REST
创建按请求 origin 拼接，模型工具返回相对路径——部署时应配置该值，例如环境变量
`OPENAGENT_SHARE_PUBLIC_BASE_URL`，保证返回的 URL 始终是可直达的绝对地址）。

模型侧由大模型调用内部工具 `create_file_transfer_url` 生成（保持原工具名），可选参数 `audience`、`mode` 与
`expiresInSeconds`；REST 侧前端可调用 `POST /api/v1/agent/files/{fileId}/share`（body 同样支持
`audience`）。两个场景不变：

- **MCP 跨系统传输**：大模型判断某个第三方 MCP 工具需要文件 URL 时调用（传 `audience="mcp"`，默认 2 小时/2 次下载），并把返回的 URL 作为参数传给该 MCP 工具。
- **用户下载/分享链接**：用户需要直接下载链接时调用，把 URL 作为分享链接交给用户；必须同时告知有效期（`expiresAt`）与下载限制（`singleUse` 链接下载一次后失效），不得表述为永久链接。

创建响应与列表项都带 `shareId`（令牌哈希），用户可通过
`GET /api/v1/agent/files/shares` 查询自己的全部分享（含已过期/已用尽的，`isActive` 标识当前可用性），
并用 `DELETE /api/v1/agent/files/shares/{shareId}` 撤销；撤销即删除记录，令牌立即 404。
明文令牌只在创建响应的 `url` 中出现一次，列表不返回 URL。

响应示例：

```json
{
  "shareId": "267ed5e832a60f49d48508f763de6c546b09c51f4c3be9993b4f0cafe5600de5",
  "fileId": "c745f86af1e44857ac63d463f0bc0495",
  "url": "https://engine.example.com/api/v1/share/0d9a2b7c4e5f6a8b9c0d1e2f3a4b5c6d",
  "mode": "Temporary",
  "expiresAt": "2026-08-26T12:00:00Z",
  "maxDownloads": null
}
```

`fileId` 是 OpenAgent 的业务资产 ID；`url` 指向本平台分享端点，不包含 `objectKey` 或任何 S3 定位信息；
接收方无需对象存储凭据。普通上传、查询、预览、认证下载和聊天流程不生成分享链接。

> 兼容说明：对象存储层的 `CreateReadUrlAsync`（S3 预签名）仍作为底层能力保留，但所有对外链路
>（模型工具与 REST 分享）不再使用预签名 URL；`PublicServiceUrl` 配置与
> `OPENAGENT_S3_PUBLIC_SERVICE_URL` 环境变量已移除，S3 只需配置内部服务地址
> `ServiceUrl`，无需公网域名。
