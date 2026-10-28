namespace OpenAgent.Contracts.Requests;

public class AgentResponse
{
    public required string Content { get; init; }
    public List<Citation>? Citations { get; init; }
    public List<ToolCallLog>? ToolCalls { get; init; }
    public TokenUsage? TokenUsage { get; init; }
    public string? ModelId { get; init; }
    public string? TraceId { get; init; }
    public bool Success { get; init; } = true;
    public AgentErrorCode? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public class Citation
{
    public required string SourceId { get; init; }
    public required string Text { get; init; }
    public double? RelevanceScore { get; init; }
}

public class ToolCallLog
{
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public string? Result { get; init; }
    public long ElapsedMs { get; init; }
    public bool IsSuccess { get; init; }
}

public class TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public int? CachedInputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
}
