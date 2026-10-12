namespace OpenAgent.Contracts.Runtime;

public interface IRuntimeDiagnostics
{
    Task<RuntimeProbeResult> TestAsync(string harness, CancellationToken cancellationToken = default);
}

public sealed record RuntimeProbeResult(bool Success, string Stage, string Message,
    bool? LoadSession = null, string? AgentName = null);
