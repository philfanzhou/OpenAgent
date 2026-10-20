namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class PendingReadStream(bool throwOnCancel = false) : MemoryStream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCount
    {
        get; private set;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<int> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            pending.TrySetCanceled(cancellationToken);
            if (throwOnCancel)
            {
                throw new IOException("Injected cancellation callback failure.");
            }
        });
        Entered.TrySetResult();
        return await pending.Task;
    }

    public override ValueTask DisposeAsync()
    {
        DisposeCount++;
        return base.DisposeAsync();
    }
}
