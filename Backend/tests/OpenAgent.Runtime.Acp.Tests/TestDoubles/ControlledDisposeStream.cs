namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class ControlledDisposeStream(bool block = false, bool fail = false) : MemoryStream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IOException Failure { get; } = new("Injected disposal failure.");
    public int DisposeCount { get; private set; }

    public override async ValueTask DisposeAsync()
    {
        DisposeCount++;
        Entered.TrySetResult();
        if (block)
        {
            await Release.Task;
        }

        await base.DisposeAsync();
        if (fail)
        {
            throw Failure;
        }
    }
}
