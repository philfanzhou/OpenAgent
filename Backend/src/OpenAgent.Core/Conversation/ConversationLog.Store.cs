using Microsoft.Extensions.Logging;
namespace OpenAgent.Core.Conversation;

internal static partial class ConversationLog
{
    // --- RedisConversationStore (1120-1135) ---

    [LoggerMessage(EventId = 1100, Level = LogLevel.Error, Message = "Failed to load messages from Redis for {ConversationId}")]
    public static partial void LoadMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Failed to load paged messages from Redis for {ConversationId}")]
    public static partial void LoadPagedMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Failed to load record from Redis for {ConversationId}")]
    public static partial void LoadRecordFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "Failed to create conversation record in Redis for {ConversationId}")]
    public static partial void CreateRecordFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Debug, Message = "Failed to renew tenant index TTL for {TenantId}")]
    public static partial void TenantIndexTtlRenewFailed(ILogger logger, Exception exception, string tenantId);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Error, Message = "Failed to append messages in Redis for {ConversationId}")]
    public static partial void AppendMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Error, Message = "Failed to update status in Redis for {ConversationId}")]
    public static partial void UpdateStatusFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Warning, Message = "Failed to get Redis database")]
    public static partial void GetDatabaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Warning, Message = "Failed to list conversations from Redis for tenant {TenantId}")]
    public static partial void ListConversationsFailed(ILogger logger, Exception exception, string tenantId);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Warning, Message = "Failed to search conversations from Redis for tenant {TenantId}")]
    public static partial void SearchConversationsFailed(ILogger logger, Exception exception, string tenantId);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Error, Message = "Failed to soft-delete conversation in Redis for {ConversationId}")]
    public static partial void SoftDeleteFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Information,
        Message = "ConversationStore Metrics - Hits: {Hits}, Misses: {Misses}, MessagesLoaded: {MessagesLoaded}, MessagesWritten: {MessagesWritten}, ReadFailures: {ReadFailures}, WriteFailures: {WriteFailures}, ColdArchiveSuccesses: {ColdArchiveSuccesses}, ColdArchiveFailures: {ColdArchiveFailures}, AvgReadMs: {AvgReadMs:F1}, AvgWriteMs: {AvgWriteMs:F1}, AvgColdArchiveMs: {AvgColdMs:F1}")]
    public static partial void MetricsSnapshot(
        ILogger logger,
        long hits,
        long misses,
        long messagesLoaded,
        long messagesWritten,
        long readFailures,
        long writeFailures,
        long coldArchiveSuccesses,
        long coldArchiveFailures,
        double avgReadMs,
        double avgWriteMs,
        double avgColdMs);

    // --- DualWriteConversationStore (1140-1152) ---

    [LoggerMessage(EventId = 1112, Level = LogLevel.Warning, Message = "Failed to load messages from cold archive for {ConversationId}")]
    public static partial void ColdArchiveLoadMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1113, Level = LogLevel.Warning, Message = "Failed to load paged messages from cold archive for {ConversationId}")]
    public static partial void ColdArchiveLoadPagedMessagesFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1114, Level = LogLevel.Warning, Message = "Failed to get conversation from cold archive for {ConversationId}")]
    public static partial void ColdArchiveGetRecordFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1115, Level = LogLevel.Warning, Message = "Hot store create failed for {ConversationId}, skipping cold archive")]
    public static partial void HotStoreCreateFailed(ILogger logger, string conversationId);

    [LoggerMessage(EventId = 1116, Level = LogLevel.Error, Message = "Message archive failed for conversation {ConversationId}. Hot store is consistent.")]
    public static partial void MessageArchiveFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1117, Level = LogLevel.Error, Message = "SQL Server archive failed for conversation {ConversationId} version {Version}. Hot store is consistent. Cold store needs compensation.")]
    public static partial void SqlArchiveFailed(ILogger logger, Exception exception, string conversationId, int version);

    [LoggerMessage(EventId = 1118, Level = LogLevel.Warning, Message = "Warm-up append failed for {ConversationId}: {Reason}")]
    public static partial void WarmUpAppendFailed(ILogger logger, string conversationId, string? reason);

    [LoggerMessage(EventId = 1119, Level = LogLevel.Warning, Message = "Failed to warm up conversation {ConversationId} from cold archive")]
    public static partial void WarmUpConversationFailed(ILogger logger, Exception exception, string conversationId);

    [LoggerMessage(EventId = 1120, Level = LogLevel.Warning, Message = "Failed to warm up messages for conversation {ConversationId} from cold archive")]
    public static partial void WarmUpMessagesFailed(ILogger logger, Exception exception, string conversationId);

    // --- RedisConversationLock (1200-1202) ---

    [LoggerMessage(EventId = 1121, Level = LogLevel.Warning, Message = "[ConversationLock] Heartbeat failed for {Key} (owner={OwnerPrefix})")]
    public static partial void HeartbeatFailed(ILogger logger, Exception exception, string key, string ownerPrefix);

    [LoggerMessage(EventId = 1122, Level = LogLevel.Warning, Message = "[ConversationLock] Extend failed (lock no longer held) for {Key} (owner={OwnerPrefix})")]
    public static partial void ExtendFailed(ILogger logger, string key, string ownerPrefix);

    [LoggerMessage(EventId = 1123, Level = LogLevel.Warning, Message = "[ConversationLock] Release failed for {Key} (owner={OwnerPrefix})")]
    public static partial void ReleaseFailed(ILogger logger, Exception exception, string key, string ownerPrefix);
}
