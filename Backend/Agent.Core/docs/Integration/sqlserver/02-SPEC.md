# SQL Server — 规格说明

## 表结构

### ConversationRecords

会话元数据表（以 `ConversationId` 为主键）：

```sql
CREATE TABLE ConversationRecords (
    ConversationId  NVARCHAR(128) NOT NULL PRIMARY KEY,
    TenantId        NVARCHAR(128) NOT NULL,
    UserId          NVARCHAR(128) NOT NULL,
    AgentId         NVARCHAR(128) NULL,
    TraceId         NVARCHAR(128) NULL,
    Version         INT NOT NULL DEFAULT 1,
    Status          INT NOT NULL DEFAULT 0,
    CreatedAt       DATETIMEOFFSET NOT NULL,
    UpdatedAt       DATETIMEOFFSET NOT NULL,
    LastMessageAt   DATETIMEOFFSET NOT NULL,
    MessageCount    INT NOT NULL DEFAULT 0,
    Title           NVARCHAR(256) NULL,              -- 会话标题
    IsDeletedByUser BIT NOT NULL DEFAULT 0,          -- 用户软删除标记
    DeletedAt       DATETIMEOFFSET NULL,             -- 用户删除时间
    ArchivedAt      DATETIMEOFFSET NOT NULL           -- 归档入库时间
);

CREATE INDEX IX_ConversationRecords_Tenant_User ON ConversationRecords (TenantId, UserId, UpdatedAt);
CREATE INDEX IX_ConversationRecords_Tenant_Agent ON ConversationRecords (TenantId, AgentId, UpdatedAt);
CREATE INDEX IX_ConversationRecords_Tenant_Deleted ON ConversationRecords (TenantId, IsDeletedByUser, LastMessageAt);
CREATE INDEX IX_ConversationRecords_ArchivedAt ON ConversationRecords (ArchivedAt);
```

### ConversationMessages

消息行级表（以 `ConversationId` + `Sequence` 为复合主键）：

```sql
CREATE TABLE ConversationMessages (
    ConversationId NVARCHAR(128) NOT NULL,
    Sequence       INT NOT NULL,
    MessageId      NVARCHAR(128) NOT NULL,
    Role           NVARCHAR(16) NOT NULL,
    Content        NVARCHAR(MAX) NOT NULL,
    ToolCallId     NVARCHAR(128) NULL,
    ToolName       NVARCHAR(128) NULL,
    Timestamp      DATETIMEOFFSET NOT NULL,
    MetadataJson   NVARCHAR(MAX) NULL,
    TenantId       NVARCHAR(128) NOT NULL,

    PRIMARY KEY (ConversationId, Sequence)
);

CREATE INDEX IX_Messages_Tenant_Time ON ConversationMessages (TenantId, Timestamp);
```

### TVP 类型

用于批量消息插入的表值参数类型：

```sql
CREATE TYPE dbo.ConversationMessageType AS TABLE (
    ConversationId NVARCHAR(128),
    Sequence       INT,
    MessageId      NVARCHAR(128),
    Role           NVARCHAR(16),
    Content        NVARCHAR(MAX),
    ToolCallId     NVARCHAR(128),
    ToolName       NVARCHAR(128),
    Timestamp      DATETIMEOFFSET,
    MetadataJson   NVARCHAR(MAX)
);
```

## UPSERT 逻辑

### 会话记录：MERGE INTO

```sql
MERGE INTO ConversationRecords AS target
USING (VALUES (@ConversationId, @TenantId, ...)) AS source (...)
ON target.ConversationId = source.ConversationId
WHEN MATCHED THEN
    UPDATE SET AgentId = source.AgentId, TraceId = source.TraceId, ...
WHEN NOT MATCHED THEN
    INSERT (ConversationId, TenantId, ...) VALUES (source.ConversationId, source.TenantId, ...);
```

### 消息行：MERGE INTO + TVP

```sql
MERGE INTO ConversationMessages AS target
USING @messages AS source
ON target.ConversationId = source.ConversationId AND target.Sequence = source.Sequence
WHEN NOT MATCHED THEN
    INSERT (ConversationId, Sequence, MessageId, Role, Content,
            ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
    VALUES (source.ConversationId, source.Sequence, source.MessageId, source.Role, source.Content,
            source.ToolCallId, source.ToolName, source.Timestamp, source.MetadataJson, @TenantId);
```

## 配置选项

```csharp
// ConversationStoreOptions 中与冷归档相关的配置
public bool EnableColdArchive { get; set; } = true;            // 是否启用冷归档
public string? ColdArchiveConnectionString { get; set; }       // 连接字符串
public string ColdArchiveProvider { get; set; } = "SqlServer"; // 冷归档提供器
public int ColdArchiveRetryCount { get; set; } = 3;            // 重试次数
public int ColdArchiveRetryDelayMs { get; set; } = 1000;       // 重试初始延迟（ms）

// 数据分层管理配置
public int MessageRetentionDays { get; set; } = 90;              // 消息活跃期保留天数
public int ArchiveMigrationIntervalMinutes { get; set; } = 60;   // 迁移任务间隔（分钟）
public int ArchiveMigrationBatchSize { get; set; } = 100;        // 每批迁移会话数

// 会话标题配置
public int TitleTruncateLength { get; set; } = 50;               // 标题截取字符数
public bool EnableTitleSummarization { get; set; } = true;       // 是否启用 LLM 摘要标题
```

## 归档表（ConversationMessagesArchive）

超期消息从 `ConversationMessages` 迁移至归档表，表结构完全一致：

```sql
CREATE TABLE ConversationMessagesArchive (
    ConversationId NVARCHAR(128) NOT NULL,
    Sequence       INT NOT NULL,
    MessageId      NVARCHAR(128) NOT NULL,
    Role           NVARCHAR(16) NOT NULL,
    Content        NVARCHAR(MAX) NOT NULL,
    ToolCallId     NVARCHAR(128) NULL,
    ToolName       NVARCHAR(128) NULL,
    Timestamp      DATETIMEOFFSET NOT NULL,
    MetadataJson   NVARCHAR(MAX) NULL,
    TenantId       NVARCHAR(128) NOT NULL,

    PRIMARY KEY (ConversationId, Sequence)
) WITH (DATA_COMPRESSION = PAGE);

CREATE INDEX IX_ArchiveMessages_Tenant_Time ON ConversationMessagesArchive (TenantId, Timestamp);
CREATE INDEX IX_ArchiveMessages_ConversationId ON ConversationMessagesArchive (ConversationId);
```

- 启用页压缩（Page Compression），空间占用降低 50-70%
- 只写不读（审计调取时查询）
- 审计搜索跨表查询：`UNION ALL ConversationMessages + ConversationMessagesArchive`

## 数据分层迁移

后台 IHostedService 定时任务，按 `ArchiveMigrationIntervalMinutes` 间隔执行：

```sql
-- 1. 扫描超期会话（ArchivedAt < now - MessageRetentionDays）
SELECT TOP (@BatchSize) ConversationId
FROM ConversationRecords
WHERE ArchivedAt < DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME())
  AND EXISTS (SELECT 1 FROM ConversationMessages WHERE ConversationId = ConversationRecords.ConversationId)

-- 2. 同事务内迁移每个会话的消息
BEGIN TRAN
  INSERT INTO ConversationMessagesArchive SELECT * FROM ConversationMessages WHERE ConversationId = @id
  DELETE FROM ConversationMessages WHERE ConversationId = @id
COMMIT
```

## 消息存储

- 消息以行级方式存储在 `ConversationMessages` 表中（非 JSON blob）
- `Metadata` 字段独立 JSON 序列化到 `MetadataJson` 列
- 批量写入通过 TVP `dbo.ConversationMessageType` 实现
- 使用 `System.Text.Json` 进行 Metadata 序列化
