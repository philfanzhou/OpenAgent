# FileAssets 与 ConversationFileReferences

`FileAssets` 是文件资产元数据的唯一事实源，由 `SqliteFileAssetRepository` 自动建表。S3/MinIO 不承担租户、用户、会话或状态的事实来源。

| 字段 | 说明 |
|------|------|
| FileId | 不透明文件 ID |
| TenantId | 租户归属；对象键使用其 SHA-256 分区 |
| OwnerUserId | 文件资产所有者 |
| FileName / MediaType / Length / Sha256 | 文件描述与完整性摘要 |
| ObjectKey | 私有对象定位符，不通过 API 返回 |
| Source / State | 上传来源与 `Pending`、`Ready`、`Failed` 生命周期 |

`ConversationFileReferences` 只保存 `ConversationId` 和 `FileId` 的多对多关系。会话引用不改变文件所有权；后续的删除、分享和权限策略必须以 `FileAssets` 为准。
