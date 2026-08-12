namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Optional hot copy of conversations. The durable <see cref="IConversationStore"/>
/// remains the unique source of truth.
/// </summary>
public sealed class ConversationCacheOptions
{
    public const string SectionName = "ConversationCache";

    public bool Enabled { get; set; } = true;
    public int TimeToLiveMinutes { get; set; } = 30;
    public string? ConnectionString { get; set; }
}
