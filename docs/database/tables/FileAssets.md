# FileAssets

`openagent.file_assets` 是用户文件资产元数据的唯一事实源，由 EF Core migration 建立。每条记录包含租户、所有者、文件名、MIME、长度、SHA-256、实际 S3 `ObjectKey`、来源和 `Pending`/`Ready`/`Failed` 状态。`FileId` 是应用层资产主键；S3 不存在与它等价的独立 ID。

资产独立于会话创建；上传成功后，文件只在随后的用户消息持久化时建立会话和消息引用。删除、分享、复用和未来权限治理都应以资产所有权与引用表为边界，而不是以对象键或临时聊天请求为边界。跨系统分享使用短期签名 URL；`ObjectKey` 作为存储定位信息返回，但不作为第三方授权凭据。
