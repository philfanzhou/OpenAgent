using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Rag;

public sealed class RagConnectionTestRequest
{
    public RagInstanceConfig Instance { get; init; } = new();
}

public sealed class RagConnectionTestResult
{
    public bool Success { get; init; }
    public bool Connected { get; init; }
    public int? StatusCode { get; init; }
    public long LatencyMs { get; init; }
    public string? Error { get; init; }
    public string? TraceId { get; init; }
}
