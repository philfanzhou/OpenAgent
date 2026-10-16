namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>Deterministic Stream faults for SDK lifecycle scenarios; contains no protocol implementation.</summary>
internal sealed class ProbeStream : MemoryStream
{
    internal bool ThrowOnCancel { get; init; }
    internal bool GateDispose { get; init; }
    internal bool FaultWrite { get; init; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!ThrowOnCancel)
        {
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        TaskCompletionSource<int> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            pending.TrySetCanceled(cancellationToken);
            throw new IOException("Injected cancellation callback failure.");
        });
        Entered.TrySetResult();
        return await pending.Task.ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (FaultWrite)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected output failure.");
        }

        await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        if (GateDispose)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
