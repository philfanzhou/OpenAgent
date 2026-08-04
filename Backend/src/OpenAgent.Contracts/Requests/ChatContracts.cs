namespace OpenAgent.Contracts.Requests;

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Context { get; set; }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
}
