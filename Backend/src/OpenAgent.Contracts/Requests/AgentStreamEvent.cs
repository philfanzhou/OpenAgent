namespace OpenAgent.Contracts.Requests;

using OpenAgent.Contracts.Approvals;

public enum AgentStreamEventType
{
    Content,
    Reasoning,
    ToolCall,
    ToolResult,
    Approval,
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
    public string? Status { get; init; }
    public HumanApprovalRequest? Approval { get; init; }
}
