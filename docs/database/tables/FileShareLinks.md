# FileShareLinks

`openagent.file_share_links` 是平台文件分享链接的唯一事实源，由 EF Core migration 建立。每条记录包含令牌哈希（`ShareIdHash`，主键，SHA-256 hex）、`FileId`、租户、所有者、分享时刻的 `ObjectKey`/文件名/MIME/长度快照、`ExpiresAt`、`MaxDownloads`（null 表示不限）与 `DownloadCount`。

- 数据库不保存明文令牌；下载请求按令牌哈希定位记录，与第三方 API Key 的存储约定一致。
- 核销使用条件 `UPDATE`（未过期且未超次数才 `DownloadCount + 1`）保证原子性，多实例下“单次下载”语义严格。
- 链接不存在、已过期或已用尽下载次数时下载端点统一返回 404，不暴露链接状态。
- 删除、治理应以 `FileId` 外键（Restrict）与租户边界为准；记录保留到期后可按 `ExpiresAt` 清理。
