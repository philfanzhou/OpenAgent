namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Compression summary metadata persisted on a conversation record.
/// </summary>
public sealed class ContextSummary
{
    /// <summary>
    /// Stable identifier for the compression attempt.
    /// </summary>
    public required string CompressionId { get; init; }

    /// <summary>
    /// MAF compaction strategy used for the attempt.
    /// </summary>
    public required string Strategy { get; init; }

    /// <summary>
    /// Whether the attempt was initiated by the runtime or a user.
    /// </summary>
    public required string Trigger { get; init; }

    /// <summary>
    /// Final attempt status: Succeeded or Failed.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Generated summary text when the strategy produces one.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Human-readable description of the compacted model context.
    /// </summary>
    public string? Result { get; init; }

    /// <summary>
    /// Failure detail when compaction was not applied.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Timestamp of the last compression.
    /// </summary>
    public DateTimeOffset LastCompressedAt { get; init; }

    /// <summary>
    /// Number of original messages that were compressed.
    /// </summary>
    public int CompressedMessageCount { get; init; }

    /// <summary>
    /// First original conversation message sequence included in the attempt.
    /// </summary>
    public int OriginalStartSequence { get; init; }

    /// <summary>
    /// Last original conversation message sequence included in the attempt.
    /// </summary>
    public int OriginalEndSequence { get; init; }

    /// <summary>
    /// Estimated token count after compression.
    /// </summary>
    public int TokenCount { get; init; }

    /// <summary>
    /// True when a failed attempt restored the original model context.
    /// </summary>
    public bool OriginalHistoryRestored { get; init; }

    /// <summary>
    /// Last persisted message sequence represented by the compacted result.
    /// </summary>
    public int SourceEndSequence { get; init; }

    /// <summary>
    /// Compacted model-context projection. Full conversation messages remain authoritative.
    /// </summary>
    public List<ConversationMessage> CompactedMessages { get; init; } = [];
}
