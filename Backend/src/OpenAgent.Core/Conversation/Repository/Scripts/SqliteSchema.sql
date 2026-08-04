CREATE TABLE IF NOT EXISTS ConversationRecords (
    ConversationId TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL,
    UserId TEXT NOT NULL,
    AgentId TEXT,
    TraceId TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    Status INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    LastMessageAt TEXT NOT NULL,
    MessageCount INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS IX_Records_Tenant_User
    ON ConversationRecords (TenantId, UserId, UpdatedAt);
CREATE INDEX IF NOT EXISTS IX_Records_Tenant_Agent
    ON ConversationRecords (TenantId, AgentId, UpdatedAt);

CREATE TABLE IF NOT EXISTS ConversationMessages (
    ConversationId TEXT NOT NULL,
    Sequence INTEGER NOT NULL,
    MessageId TEXT NOT NULL,
    Role TEXT NOT NULL,
    Content TEXT NOT NULL,
    ToolCallId TEXT,
    ToolName TEXT,
    Timestamp TEXT NOT NULL,
    MetadataJson TEXT,
    TenantId TEXT NOT NULL,
    PRIMARY KEY (ConversationId, Sequence)
);

CREATE INDEX IF NOT EXISTS IX_Messages_Tenant_Time
    ON ConversationMessages (TenantId, Timestamp);
