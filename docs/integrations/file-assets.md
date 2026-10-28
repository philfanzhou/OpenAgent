# 文件资产与对象存储

文件资产独立于会话：PostgreSQL 保存资产、所有者、状态、实际 S3 `ObjectKey` 和引用，S3/MinIO 只保存原始字节。聊天请求只传递 `fileIds`；执行期由文件服务读取内容给模型，消息与数据库中都不再存在旧附件对象或文件字节。

```text
POST /files -> PostgreSQL FileAssets(Pending) -> S3/MinIO -> FileAssets(Ready)
POST /chat/stream { fileIds } -> read ready files -> model -> message and file references
```

`FileAssetService` 是上传、读取和模型函数的唯一入口，`S3FileObjectStore` 是对象存储适配器。未配置对象存储时文件端点返回依赖不可用；不存在旧的 multipart 聊天降级路径。

文件类型不做准入限制：任意扩展名（含 `.py`/`.js` 等脚本与未知扩展名）均可上传入库。`OpenAgent.Contracts.Files.FileMediaTypeCatalog` 仅作为类型推断目录（扩展名 → 规范媒体类型，含等价别名）：缺省或通用媒体类型（octet-stream）按扩展名推断规范类型，目录外扩展名按 `application/octet-stream` 存储，调用方显式声明的具体类型原样保留，不做扩展名一致性校验。`download_file` 的无扩展名补全与对象内容下发的 `Content-Type` 推断也由同一目录派生（目录外扩展名下发 `application/octet-stream`）；内容下发端点统一附加 `X-Content-Type-Options: nosniff`。read_file 的文本判定（`text/*` 及以文本存储的结构化类型）同样由此派生，目录外扩展名按二进制处理，走元数据信封而非报错。目录当前可识别 html/htm/css、md、csv、json、xml、txt、svg、drawio、png/jpg/jpeg/jps/gif/webp、pdf、zip、pptx、xlsx；新增可识别类型只需追加一条记录。沙箱执行同样不按类型过滤产物：仅空文件或超出存储层限制（如大小）的输出会被跳过并在工具结果的 `skippedFiles` 中报告原因，不会导致整个执行失败。

LLM Profile 选择 `Multimodal` 时，聊天请求中的 `image/*` 资产会在执行期以内联图片内容发送给模型；默认每次最多 4 张、每张不超过 4 MiB，可通过 `FileAssets:MaxInlineImageCount` 和 `FileAssets:MaxInlineImageBytes` 调整。历史消息重放时仅最近 `FileAssets:InlineImageHistoryTurns`（默认 2）个用户轮次重新内联图片，更早轮次的图片只保留文件描述符，避免长会话每次请求重复携带全部历史图片 token；当轮用户消息不受该窗口限制。`Text` Profile、非图片文件、超限图片和对象读取失败均不会发送二进制，只保留 fileId manifest。当前未开放音频、视频等其他多模态输入。

内联降采样（`InlineImageOptimizer`）采用"先验证、后解码"的管线：按内容嗅探真实格式（声明媒体类型可被任意伪造，以内探结果为准且必须与声明一致）、读取头信息获取真实尺寸，随后校验单帧像素数（`InlineImageMaxPixels`，默认 2^26）与解码内存预算（`InlineImageMaxDecodedBytes` = 宽×高×4 字节估算，默认 256 MiB），全部通过后才解码并等比缩放到 `InlineImageMaxLongEdge`。解码基于 SkiaSharp 且只取首帧：多帧图（动画 WebP/APNG）的解码内存与单帧图相同，帧数炸弹天然受限，需降采样的动画图输出为静态首帧（视觉模型只消费首帧）；尺寸已达标的小图按原字节内联（动画保持原样）。任一环节不通过（格式不符、数据畸形、超限）会以 `InlineImageRejectedException` 明确拒绝并记录告警日志（含 fileId 与具体原因），该图不再内联、模型仍可通过工具读取原文件——不会回退为把原始字节继续发给模型，防止小体积头部声明超大尺寸的解压炸弹。上述限制只作用于图片内联处理，普通文件上传与存储不受影响。

模型通过 `write_file` 生成文件时（或经沙箱执行产物登记），产物会登记为 `FileAsset`，但不会自动出现在 assistant 消息中。模型调用 `publish_files` 并传入一个或多个 `fileId` 后，选中的资产才会关联到当前 assistant 消息；这允许模型保留中间产物、批量发布 Markdown 与图片。消息只要带有文件引用，续接会话时都会把对应文件重新注入原消息的模型上下文（user、assistant 均适用）；模型还可以调用 `list_files` 发现当前会话引用的文件，再按需 `read_file` 或 `publish_files`。是否向用户交付仍由消息级发布引用决定。首条消息也必须沿用前端创建的 conversationId，确保上传文件引用和本次模型请求使用同一会话范围。前端可使用消息中的 `fileId` 调用认证下载端点；模型不应直接输出未经授权的 MinIO URL。

启用文件资产后，模型还可以调用 `download_file`。该函数只接收公开的 HTTP(S) 地址，下载结果写入当前租户、用户和会话范围，并建立会话引用；工具返回 `fileId`、文件名、MIME 和长度。下载器会限制响应大小、超时和重定向次数，并拒绝回环、链路本地、私有网段及多播地址。

本地依赖由仓库根目录 `deploy/infrastructure/docker-compose.yml` 提供 PostgreSQL、MinIO 与 bucket 初始化。开发环境中使用 `ConnectionStrings:OpenAgentDatabase` 和 `FileAssets:ObjectStorage` 配置。

| 端点 | 用途 |
|---|---|
| `POST /api/v1/agent/files` | 上传一个独立资产，返回 `fileId` |
| `GET /api/v1/agent/files/{fileId}` | 读取资产元数据 |
| `GET /api/v1/agent/files/{fileId}/content` | 认证预览内容 |
| `GET /api/v1/agent/files/{fileId}/download` | 认证下载 |
| `POST /api/v1/agent/files/{fileId}/share` | 创建分享链接（认证，body 指定 `mode`/`expiresInSeconds`） |
| `GET /api/v1/agent/files/shares` | 查询当前用户在该租户下仍有效的分享链接（认证，按创建时间倒序；已过期/已用尽/已撤销的不返回） |
| `DELETE /api/v1/agent/files/shares/{shareId}` | 撤销一条分享链接（认证，软删除）；标记失效后令牌立即 404，不存在/已失效/非本人统一 404 |
| `GET /api/v1/share/{token}` | 匿名分享下载；令牌即凭证，过期或超次数统一 404 |

权限校验通过 `FileAssetScope` 的 TenantId/OwnerUserId 边界在 `FileAssetService` 内强制执行（缺失时抛 `TenantDataIsolationException`）。

## HTML 附件在线预览

前端聊天界面对 `text/html` 附件提供在线预览：预览弹窗中不再展示源码文本，而是直接渲染为 HTML 页面。实现路径与图片预览一致——前端经认证 `GET /files/{fileId}/content` 拉取字节后转为 blob URL 交给渲染层（浏览器 iframe 无法携带 `Authorization` 头，不能直接引用端点地址），内容按需懒加载，不随会话预取。

HTML 在 `sandbox="allow-scripts"`（不含 `allow-same-origin`）的 iframe 中渲染：页面脚本可运行以满足图表类报告，但运行于 opaque origin，无法访问应用 DOM、localStorage/sessionStorage（会话令牌所在）或 Cookie，也没有弹窗、表单与顶层导航能力。平台不提供“新窗口打开”式预览：顶层 blob 页面会继承应用源，脚本即可读取会话令牌。相对路径子资源（如 `./chart.png`）在 blob 基址下无法解析会静默失败，因此预览面向自包含 HTML（如 `mcp-data-report` 产出的 `report.html`）；引用外部资源的文件下载后查看。

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
`expiresInSeconds`。链接的查询与撤销不再暴露为模型工具（模型无从得知 `shareId`，曾导致误传空参数），
仅保留 REST 侧由前端调用：`POST /api/v1/agent/files/{fileId}/share`（body 同样支持
`audience`）创建、`GET /api/v1/agent/files/shares` 查询、`DELETE /api/v1/agent/files/shares/{shareId}` 撤销。
`create_file_transfer_url` 的工具描述会提示模型：链接创建后无法由模型撤销，应优先选择较短有效期，
并向用户说明有效期与下载次数限制。两个场景不变：

- **MCP 跨系统传输**：大模型判断某个第三方 MCP 工具需要文件 URL 时调用（传 `audience="mcp"`，默认 2 小时/2 次下载），并把返回的 URL 作为参数传给该 MCP 工具。
- **用户下载/分享链接**：用户需要直接下载链接时调用，把 URL 作为分享链接交给用户；必须同时告知有效期（`expiresAt`）与下载限制（`singleUse` 链接下载一次后失效），不得表述为永久链接。

创建响应与列表项都带 `shareId`（令牌哈希），用户可通过
`GET /api/v1/agent/files/shares` 查询自己仍有效的分享（已过期、已用尽或已撤销的不返回），
并用 `DELETE /api/v1/agent/files/shares/{shareId}` 撤销；撤销为软删除（把失效时间改写为当前时刻），
令牌立即 404、记录保留作审计。明文令牌只在创建响应的 `url` 中出现一次，列表不返回 URL。

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
> （模型工具与 REST 分享）不再使用预签名 URL；`PublicServiceUrl` 配置与
> `OPENAGENT_S3_PUBLIC_SERVICE_URL` 环境变量已移除，S3 只需配置内部服务地址
> `ServiceUrl`，无需公网域名。

## 图片处理依赖（SkiaSharp，MIT）

内联图片处理依赖 `SkiaSharp`（MIT 许可，无条件商用免费、无注册或构建期许可校验），
版本统一在 `Backend/Directory.Packages.props` 管理。Skia 与 Chrome 同源、由 Google
持续模糊测试，NuGet 安全记录干净（唯一一条历史公告为 2023 年 libwebp CVE-2023-4863，
早已修复）。

部署注意：SkiaSharp 含原生库 `libSkiaSharp`（Windows 随主包分发；Linux 通过显式引用的
`SkiaSharp.NativeAssets.Linux` 提供 linux-x64/glibc 资产，匹配 Engine.Host 的
`aspnet:10.0` bookworm 镜像）。`libSkiaSharp.so` 依赖系统 `libfontconfig1`，
Engine.Host Dockerfile 的构建与运行阶段已安装；若更换基础镜像或使用 Alpine（musl），
需改用 `SkiaSharp.NativeAssets.Linux.NoDependencies` 变体。
