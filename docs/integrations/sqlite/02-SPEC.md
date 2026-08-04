# SQLite — 规格说明

## 表结构

### ConversationRecords

会话元数据表（以 `ConversationId` 为主键）：

```sql
CREATE TABLE IF NOT EXISTS ConversationRecords (
    ConversationId TEXT NOT NULL PRIMARY KEY,
    TenantId       TEXT NOT NULL,
    UserId         TEXT NOT NULL,
    AgentId        TEXT,
    TraceId        TEXT,
    Version        INTEGER NOT NULL DEFAULT 1,
    Status         INTEGER NOT NULL DEFAULT 0,
    CreatedAt      TEXT NOT NULL,
    UpdatedAt      TEXT NOT NULL,
    LastMessageAt  TEXT NOT NULL,
    MessageCount   INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS IX_Records_Tenant_User ON ConversationRecords (TenantId, UserId, UpdatedAt);
CREATE INDEX IF NOT EXISTS IX_Records_Tenant_Agent ON ConversationRecords (TenantId, AgentId, UpdatedAt);
```

### ConversationMessages

消息行级表（以 `ConversationId` + `Sequence` 为复合主键）：

```sql
CREATE TABLE IF NOT EXISTS ConversationMessages (
    ConversationId TEXT NOT NULL,
    Sequence       INTEGER NOT NULL,
    MessageId      TEXT NOT NULL,
    Role           TEXT NOT NULL,
    Content        TEXT NOT NULL,
    ToolCallId     TEXT,
    ToolName       TEXT,
    Timestamp      TEXT NOT NULL,
    MetadataJson   TEXT,
    TenantId       TEXT NOT NULL,
    PRIMARY KEY (ConversationId, Sequence)
);

CREATE INDEX IF NOT EXISTS IX_Messages_Tenant_Time ON ConversationMessages (TenantId, Timestamp);
```

### 与 SQL Server 的差异

| 特性 | SQLite | SQL Server |
|------|--------|------------|
| 日期时间类型 | TEXT（ISO 8601 字符串） | DATETIMEOFFSET |
| 批量消息写入 | 事务逐行 INSERT OR IGNORE | TVP MERGE INTO |
| UPSERT 语法 | INSERT ON CONFLICT DO UPDATE | MERGE INTO |
| 建表语法 | CREATE TABLE IF NOT EXISTS | IF NOT EXISTS (sys.tables 检查) |

## UPSERT 逻辑

### 会话记录：INSERT ON CONFLICT

```sql
INSERT INTO ConversationRecords
    (ConversationId, TenantId, UserId, AgentId, TraceId,
     Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount)
VALUES
    ($ConversationId, $TenantId, $UserId, $AgentId, $TraceId,
     $Version, $Status, $CreatedAt, $UpdatedAt, $LastMessageAt, $MessageCount)
ON CONFLICT(ConversationId) DO UPDATE SET
    AgentId = excluded.AgentId,
    TraceId = excluded.TraceId,
    Version = excluded.Version,
    Status = excluded.Status,
    UpdatedAt = excluded.UpdatedAt,
    LastMessageAt = excluded.LastMessageAt,
    MessageCount = excluded.MessageCount;
```

### 消息行：事务逐行 INSERT OR IGNORE

```sql
-- 在事务中逐行执行：
INSERT OR IGNORE INTO ConversationMessages
    (ConversationId, Sequence, MessageId, Role, Content,
     ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
VALUES
    ($ConversationId, $Sequence, $MessageId, $Role, $Content,
     $ToolCallId, $ToolName, $Timestamp, $MetadataJson, $TenantId);
```

## 配置选项

SQLite 通过 `ColdArchiveProvider = "Sqlite"` 启用，使用与 SQL Server 相同的 `ColdArchiveConnectionString` 配置（连接字符串指向 SQLite 文件路径）。
