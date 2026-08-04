namespace OpenAgent.Contracts.Conversation;

public sealed class ConversationStoreOptions
{
    public const string SectionName = "ConversationStore";

    /// <summary>
    /// 执行侧历史消息窗口大小（最近 N 条）。
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>
    /// Redis 会话记录 TTL（分钟）。
    /// </summary>
    public int RedisTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Redis connection string. When empty, InMemory store is used.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// 是否启用数据库冷归档。
    /// </summary>
    public bool EnableColdArchive { get; set; } = true;

    /// <summary>
    /// 数据库连接字符串。为空则不启用冷归档。
    /// </summary>
    public string? ColdArchiveConnectionString { get; set; }

    /// <summary>
    /// 冷归档写入重试次数。
    /// </summary>
    public int ColdArchiveRetryCount { get; set; } = 3;

    /// <summary>
    /// 冷归档写入重试延迟（毫秒）。
    /// </summary>
    public int ColdArchiveRetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Cold archive provider: "SqlServer" (default) or "Sqlite".
    /// </summary>
    public string ColdArchiveProvider { get; set; } = "SqlServer";

    /// <summary>
    /// 消息活跃期保留天数。超过此天数的消息从主消息表迁移到归档消息表。默认 90 天。
    /// </summary>
    public int MessageRetentionDays { get; set; } = 90;

    /// <summary>
    /// 数据分层迁移任务执行间隔（分钟）。默认 60 分钟。
    /// </summary>
    public int ArchiveMigrationIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// 每次迁移批量处理的最大会话数。默认 100。
    /// </summary>
    public int ArchiveMigrationBatchSize { get; set; } = 100;

    /// <summary>
    /// 会话标题截取的最大字符数。首轮用户消息截取前 N 个字符作为初始标题。默认 50。
    /// </summary>
    public int TitleTruncateLength { get; set; } = 50;

    /// <summary>
    /// 是否启用 LLM 异步生成会话摘要标题。默认 true。
    /// </summary>
    public bool EnableTitleSummarization { get; set; } = true;
}
