namespace OpenAgent.Core.Tooling.Invocation;

/// <summary>Owns the per-execution exclusive gate.</summary>
internal sealed class ToolInvocationScope : IAsyncDisposable
{
    internal SemaphoreSlim ExclusiveGate { get; } = new(1, 1);

    public ValueTask DisposeAsync()
    {
        ExclusiveGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
