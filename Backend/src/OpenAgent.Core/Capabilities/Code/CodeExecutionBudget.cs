namespace OpenAgent.Core.Capabilities.Code;

/// <summary>
/// Request-scoped execution budget shared by execute_code and skill script runs,
/// so both entry points draw from the same per-request limit and neither can be
/// used to bypass the other's quota.
/// </summary>
internal sealed class CodeExecutionBudget
{
    private int _executions;

    internal bool TryConsume(int limit) =>
        Interlocked.Increment(ref _executions) <= limit;
}
