namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Compression strategy configuration passed via AgentRequest, per the
/// ContextPolicy protocol. Router decides the strategy; Core executes it.
/// </summary>
public sealed class ContextPolicy
{
    /// <summary>
    /// Compression strategy: "summarize", "sliding_window", or "none".
    /// </summary>
    public required string Strategy { get; init; }

    /// <summary>
    /// Target token limit for compressed context (0 = no limit).
    /// </summary>
    public int MaxTokens { get; init; }

    /// <summary>
    /// Number of recent turns to preserve uncompressed (default 2).
    /// </summary>
    public int PreserveRecentTurns { get; init; } = 2;

    /// <summary>
    /// Options for the "summarize" strategy. Ignored for other strategies.
    /// </summary>
    public SummarizeOptions? SummarizeOptions { get; init; }
}

public sealed class SummarizeOptions
{
    /// <summary>
    /// Model identifier for summary generation (e.g. "gpt-4o-mini").
    /// </summary>
    public string? SummaryModel { get; init; }

    /// <summary>
    /// Maximum tokens for the generated summary.
    /// </summary>
    public int MaxSummaryTokens { get; init; } = 512;
}
