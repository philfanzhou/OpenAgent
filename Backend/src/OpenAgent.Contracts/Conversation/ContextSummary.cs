namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Compression summary metadata persisted on a conversation record.
/// </summary>
public sealed class ContextSummary
{
    /// <summary>
    /// The generated summary text.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Timestamp of the last compression.
    /// </summary>
    public DateTimeOffset LastCompressedAt { get; init; }

    /// <summary>
    /// Number of original messages that were compressed.
    /// </summary>
    public int CompressedMessageCount { get; init; }

    /// <summary>
    /// Estimated token count after compression.
    /// </summary>
    public int TokenCount { get; init; }
}
