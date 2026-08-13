# FileAssets

`openagent.file_assets` 是用户文件资产元数据的唯一事实源，由 EF Core migration 建立。每条记录包含租户、所有者、会话、文件名、MIME、长度、SHA-256、对象键、来源和 `Pending`/`Ready`/`Failed` 状态。

资产上传时即绑定 `TenantId`、`OwnerUserId` 和 `ConversationId`，读取必须完整匹配这三个字段。用户消息持久化时再建立会话和消息引用，用于历史展示与生命周期治理；对象键分区是纵深防御，不替代数据库中的归属校验。

升级 migration 会为仅关联一个会话的旧资产回填 `ConversationId`；无会话引用或曾跨会话复用的旧资产保持空范围并默认不可访问，避免把历史歧义继续带入新的隔离模型。
