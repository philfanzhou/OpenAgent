# FileShareLinks

`openagent.file_share_links` 是平台文件分享链接的唯一事实源，由 EF Core migration 建立。每条记录包含令牌哈希（`ShareIdHash`，主键，SHA-256 hex）、`FileId`、租户、所有者、分享时刻的 `ObjectKey`/文件名/MIME/长度快照、`Mode`（temporary/singleUse/longTerm）、`ExpiresAt`、`MaxDownloads`（null 表示不限）与 `DownloadCount`。索引 `(TenantId, OwnerUserId, CreatedAt)` 支撑"查询当前用户的所有分享链接"。

- 数据库不保存明文令牌；下载请求按令牌哈希定位记录，与第三方 API Key 的存储约定一致。
- 核销使用条件 `UPDATE`（未过期且未超次数才 `DownloadCount + 1`）保证原子性，多实例下“单次下载”语义严格。
- 链接不存在、已过期或已用尽下载次数时下载端点统一返回 404，不暴露链接状态。
- 有效期硬上限 365 天（`FileShareOptions.MaxLifetimeLimitSeconds`），不存在永久有效的分享。
- 用户撤销分享为软删除：按 `(ShareIdHash, TenantId, OwnerUserId)` 条件把 `ExpiresAt` 改写为当前时刻（仅对仍有效的记录生效），记录保留、令牌立即 404，且不再出现于查询结果。
- 治理应以 `FileId` 外键（Restrict）与租户边界为准；已失效（过期、用尽或撤销）的记录可按 `ExpiresAt` 清理。
