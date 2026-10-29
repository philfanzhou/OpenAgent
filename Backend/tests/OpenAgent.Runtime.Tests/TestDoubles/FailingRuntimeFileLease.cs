namespace OpenAgent.Runtime.Tests.TestDoubles;

internal sealed class FailingRuntimeFileLease : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("file-lease-cleanup-failed"));
}
