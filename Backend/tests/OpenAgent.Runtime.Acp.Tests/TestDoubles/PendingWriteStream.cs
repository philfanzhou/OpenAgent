namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

internal sealed class PendingWriteStream(bool failOnRelease = false) : MemoryStream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IOException Failure { get; } = new("Injected output failure.");

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        if (failOnRelease)
        {
            throw Failure;
        }

        await base.WriteAsync(buffer, cancellationToken);
    }
}
