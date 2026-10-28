namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Summary-compression settings selected by the Agent runtime profile.
/// </summary>
public sealed class ContextPolicy
{
    /// <summary>
    /// Number of recent turns to preserve uncompressed (default 2).
    /// </summary>
    public int PreserveRecentTurns { get; init; } = 2;

    /// <summary>
    /// Options for summary generation.
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
