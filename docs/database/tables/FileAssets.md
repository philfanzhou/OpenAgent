# FileAssets

`openagent.file_assets` 是用户文件资产元数据的唯一事实源，由 EF Core migration 建立。每条记录包含租户、所有者、文件名、MIME、长度、SHA-256、实际 S3 `ObjectKey`、来源和 `Pending`/`Ready`/`Failed` 状态。`FileId` 是应用层资产主键；S3 不存在与它等价的独立 ID。

`RuntimeOutputPath` 记录 OpenCode 成品的相对路径，用于恢复当前会话中该路径最近保存的版本。其他来源文件以及已有历史资产可以没有此字段。

资产独立于会话创建；上传成功后，文件只在随后的用户消息持久化时建立会话和消息引用。删除、分享、复用和未来权限治理都应以资产所有权与引用表为边界，而不是以对象键或临时聊天请求为边界。跨系统分享使用平台自身的分享链接（见 [FileShareLinks.md](FileShareLinks.md)），不生成对象存储预签名 URL；`ObjectKey` 不作为第三方授权凭据。

## 容量与保留盘点

应用侧用 `openagent.files.stored.assets`、`openagent.files.stored.bytes` 监测写入增量，用 `openagent.files.restore.duration` 监测续聊恢复耗时。当前占用以对象存储的桶或前缀容量指标为准。以下只读查询按引用类别统计数据库中的就绪资产及逻辑字节数，可用于核对容量和确定保留策略。单独核对对象存储中的孤立对象和历史版本。

```sql
WITH classified AS (
    SELECT asset."Length", CASE
        WHEN EXISTS (SELECT 1 FROM openagent.message_file_references ref
                     WHERE ref."FileId" = asset."FileId") THEN 'message'
        WHEN EXISTS (SELECT 1 FROM openagent.file_share_links share
                     WHERE share."FileId" = asset."FileId"
                       AND share."ExpiresAt" > CURRENT_TIMESTAMP
                       AND (share."MaxDownloads" IS NULL OR share."DownloadCount" < share."MaxDownloads")) THEN 'active_share'
        WHEN EXISTS (SELECT 1 FROM openagent.conversation_file_references ref
                     WHERE ref."FileId" = asset."FileId") THEN 'conversation'
        WHEN asset."Source" = 1 AND asset."RuntimeOutputPath" IS NOT NULL THEN 'unreferenced_output'
        ELSE 'other'
    END AS retention_class
    FROM openagent.file_assets asset
    WHERE asset."State" = 1
)
SELECT retention_class, COUNT(*) AS assets, SUM("Length") AS logical_bytes
FROM classified
GROUP BY retention_class
ORDER BY retention_class;
```

`message`、`active_share` 和 `conversation` 分别保护消息附件、有效分享和续聊素材。`unreferenced_output` 用于估算可能回收的容量。清理前由运营和运维确认保留期限与规则，并在执行时重新检查引用及对象所有权。
