using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Mcp;

public sealed class McpConnectionTestRequest
{
    public string? AgentId { get; init; }
    public McpServerConfig Server { get; init; } = new();
    public string Action { get; init; } = "discover";
}

public sealed class McpConnectionTestResult
{
    public bool Success { get; init; }
    public bool Connected { get; init; }
    public bool Authorized { get; init; }
    public string Transport { get; init; } = string.Empty;
    public long LatencyMs { get; init; }
    public int ToolCount { get; init; }
    public IReadOnlyList<string> DeniedTools { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public string? TraceId { get; init; }
}

public interface IMcpConnectionTester
{
    Task<McpConnectionTestResult> TestAsync(
        McpConnectionTestRequest request,
        IAgentUserContext user,
        string? traceId,
        CancellationToken cancellationToken = default);
}
