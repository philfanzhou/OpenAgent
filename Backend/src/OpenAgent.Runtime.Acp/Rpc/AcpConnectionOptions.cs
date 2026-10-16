namespace OpenAgent.Runtime.Acp.Rpc;
public sealed record AcpConnectionOptions
{
    public int MaxPendingRequests { get; init; } = 128;
    public int MaxIncomingRequests { get; init; } = 128;
    public int MaxQueuedUpdates { get; init; } = 1024;
    public long MaxBufferedBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxSessions { get; init; } = 32;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CancelGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
