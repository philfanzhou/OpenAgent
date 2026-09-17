namespace OpenAgent.Contracts.Requests;

public enum AgentStreamEventType
{
    Content,
    Reasoning,
    ToolCall,
    ToolResult,
    Usage
}

public sealed record AgentStreamEvent
{
    public required AgentStreamEventType Type { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public object? ToolArguments { get; init; }
    public TokenUsage? Usage { get; init; }
    public string? ModelId { get; init; }
    /// <summary>本轮实际使用的追溯键，随 Usage 事件带出并写入 SSE done 帧。</summary>
    public string? TraceId { get; init; }
}
