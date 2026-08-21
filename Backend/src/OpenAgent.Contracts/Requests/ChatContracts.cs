namespace OpenAgent.Contracts.Requests;

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public List<string> FileIds { get; set; } = [];
    public Dictionary<string, object>? Context { get; set; }
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public TokenUsage? Usage { get; set; }
    public string? ModelId { get; set; }
}
