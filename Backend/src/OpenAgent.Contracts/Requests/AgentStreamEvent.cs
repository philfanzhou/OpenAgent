using OpenAgent.Contracts.Approvals;

namespace OpenAgent.Contracts.Requests;

public enum AgentStreamEventType
{
    Content,
    Reasoning,
    ToolCall,
    Usage,
    Approval
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
    public HumanApprovalRequest? Approval { get; init; }
}
