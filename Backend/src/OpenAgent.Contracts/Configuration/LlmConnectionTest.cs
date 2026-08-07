namespace OpenAgent.Contracts.Configuration;

public sealed class LlmConnectionTestRequest
{
    public LlmProviderProfile Profile { get; init; } = new();
}

public sealed class LlmConnectionTestResult
{
    public bool Success { get; init; }
    public bool Connected { get; init; }
    public int? StatusCode { get; init; }
    public long LatencyMs { get; init; }
    public string? ModelId { get; init; }
    public string? Error { get; init; }
    public string? TraceId { get; init; }
}
