using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Conversation;

internal static partial class ConversationLog
{
    // --- SqlServerConversationRepository (1160-1166) ---

    [LoggerMessage(EventId = 1161, Level = LogLevel.Error, Message = "Failed to archive conversation {ConversationId} to SQL Server")]
    public static partial void SqlServerArchiveFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1163, Level = LogLevel.Error, Message = "Failed to archive messages to SQL Server for {ConversationId}")]
    public static partial void SqlServerArchiveMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1165, Level = LogLevel.Error, Message = "Failed to initialize SQL Server conversation archive")]
    public static partial void SqlServerInitializeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1166, Level = LogLevel.Warning, Message = "SQL Server archive attempt {Attempt} failed, retrying in {Delay}ms")]
    public static partial void SqlServerRetryAttemptFailed(ILogger logger, Exception exception, int attempt, int delay);

    // --- SqliteConversationRepository (1170-1176) ---

    [LoggerMessage(EventId = 1171, Level = LogLevel.Error, Message = "Failed to initialize SQLite conversation archive")]
    public static partial void SqliteInitializeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1173, Level = LogLevel.Error, Message = "Failed to archive conversation {ConversationId} to SQLite")]
    public static partial void SqliteArchiveFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1175, Level = LogLevel.Error, Message = "Failed to archive messages to SQLite for {ConversationId}")]
    public static partial void SqliteArchiveMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1176, Level = LogLevel.Warning, Message = "SQLite archive attempt {Attempt} failed, retrying in {Delay}ms")]
    public static partial void SqliteRetryAttemptFailed(ILogger logger, Exception exception, int attempt, int delay);

    // --- ConversationQueryService (1180-1182) ---

    [LoggerMessage(EventId = 1180, Level = LogLevel.Warning, Message = "Failed to list conversations from cold archive for tenant {TenantId}")]
    public static partial void ColdArchiveListFailed(ILogger logger, Exception exception, string tenantId);

    [LoggerMessage(EventId = 1181, Level = LogLevel.Warning, Message = "Failed to search conversations from cold archive for tenant {TenantId}")]
    public static partial void ColdArchiveSearchFailed(ILogger logger, Exception exception, string tenantId);

    [LoggerMessage(EventId = 1182, Level = LogLevel.Warning, Message = "Failed to get conversation record from cold archive for {ConversationId}")]
    public static partial void QueryColdArchiveGetRecordFailed(ILogger logger, Exception exception, string conversationId);

    // --- ConversationArchiveMigrationService (1190-1199) ---

    [LoggerMessage(EventId = 1191, Level = LogLevel.Warning, Message = "Archive migration service skipped: no cold archive connection string")]
    public static partial void MigrationSkippedNoConnectionString(ILogger logger);

    [LoggerMessage(EventId = 1192, Level = LogLevel.Information, Message = "Archive migration service started, interval={Interval}min, retention={Retention}d")]
    public static partial void MigrationStarted(ILogger logger, int interval, int retention);

    [LoggerMessage(EventId = 1193, Level = LogLevel.Error, Message = "Archive migration batch failed")]
    public static partial void MigrationBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1197, Level = LogLevel.Warning, Message = "Failed to migrate conversation {ConversationId}")]
    public static partial void MigrationMigrateConversationFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1199, Level = LogLevel.Debug, Message = "Failed to release migration app lock (lock released on connection close)")]
    public static partial void MigrationReleaseLockFailed(ILogger logger, Exception exception);
}
