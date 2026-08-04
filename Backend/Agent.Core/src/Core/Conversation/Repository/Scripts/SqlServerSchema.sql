-- Conversation metadata table (no MessagesJson column)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ConversationRecords')
BEGIN
    CREATE TABLE ConversationRecords (
        ConversationId NVARCHAR(128) NOT NULL PRIMARY KEY,
        TenantId NVARCHAR(128) NOT NULL,
        UserId NVARCHAR(128) NOT NULL,
        AgentId NVARCHAR(128) NULL,
        TraceId NVARCHAR(128) NULL,
        Version INT NOT NULL DEFAULT 1,
        Status INT NOT NULL DEFAULT 0,
        CreatedAt DATETIMEOFFSET NOT NULL,
        UpdatedAt DATETIMEOFFSET NOT NULL,
        LastMessageAt DATETIMEOFFSET NOT NULL,
        MessageCount INT NOT NULL DEFAULT 0,
        Title NVARCHAR(256) NULL,
        IsDeletedByUser BIT NOT NULL DEFAULT 0,
        DeletedAt DATETIMEOFFSET NULL,
        ArchivedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE INDEX IX_ConversationRecords_Tenant_User
        ON ConversationRecords (TenantId, UserId, UpdatedAt);
    CREATE INDEX IX_ConversationRecords_Tenant_Agent
        ON ConversationRecords (TenantId, AgentId, UpdatedAt);
    CREATE INDEX IX_ConversationRecords_Tenant_Deleted
        ON ConversationRecords (TenantId, IsDeletedByUser, LastMessageAt);
    CREATE INDEX IX_ConversationRecords_ArchivedAt
        ON ConversationRecords (ArchivedAt);
END

-- Row-level message table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ConversationMessages')
BEGIN
    CREATE TABLE ConversationMessages (
        ConversationId NVARCHAR(128) NOT NULL,
        Sequence INT NOT NULL,
        MessageId NVARCHAR(128) NOT NULL,
        Role NVARCHAR(16) NOT NULL,
        Content NVARCHAR(MAX) NOT NULL,
        ToolCallId NVARCHAR(128) NULL,
        ToolName NVARCHAR(128) NULL,
        Timestamp DATETIMEOFFSET NOT NULL,
        MetadataJson NVARCHAR(MAX) NULL,
        TenantId NVARCHAR(128) NOT NULL,

        PRIMARY KEY (ConversationId, Sequence)
    );

    CREATE INDEX IX_Messages_Tenant_Time
        ON ConversationMessages (TenantId, Timestamp);
END

-- Archived message table (same schema as ConversationMessages, with PAGE compression)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ConversationMessagesArchive')
BEGIN
    CREATE TABLE ConversationMessagesArchive (
        ConversationId NVARCHAR(128) NOT NULL,
        Sequence INT NOT NULL,
        MessageId NVARCHAR(128) NOT NULL,
        Role NVARCHAR(16) NOT NULL,
        Content NVARCHAR(MAX) NOT NULL,
        ToolCallId NVARCHAR(128) NULL,
        ToolName NVARCHAR(128) NULL,
        Timestamp DATETIMEOFFSET NOT NULL,
        MetadataJson NVARCHAR(MAX) NULL,
        TenantId NVARCHAR(128) NOT NULL,

        PRIMARY KEY (ConversationId, Sequence)
    ) WITH (DATA_COMPRESSION = PAGE);

    CREATE INDEX IX_ArchiveMessages_Tenant_Time
        ON ConversationMessagesArchive (TenantId, Timestamp);
    CREATE INDEX IX_ArchiveMessages_ConversationId
        ON ConversationMessagesArchive (ConversationId);
END

-- TVP type for bulk message inserts
IF NOT EXISTS (SELECT * FROM sys.types WHERE name = 'ConversationMessageType')
BEGIN
    CREATE TYPE dbo.ConversationMessageType AS TABLE (
        ConversationId NVARCHAR(128),
        Sequence INT,
        MessageId NVARCHAR(128),
        Role NVARCHAR(16),
        Content NVARCHAR(MAX),
        ToolCallId NVARCHAR(128),
        ToolName NVARCHAR(128),
        Timestamp DATETIMEOFFSET,
        MetadataJson NVARCHAR(MAX)
    );
END
