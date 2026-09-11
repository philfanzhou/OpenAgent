namespace OpenAgent.Contracts.Requests;

using OpenAgent.Contracts.Approvals;

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public List<string> FileIds { get; set; } = [];
    public Dictionary<string, object>? Context { get; set; }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public TokenUsage? Usage { get; set; }
    public string? ModelId { get; set; }
    public HumanApprovalRequest? Approval { get; set; }
}
