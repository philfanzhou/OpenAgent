namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>
/// The connection ended before the peer answered. Distinct from a remote error: whether the
/// remote work happened is unknown, so callers must not treat the operation as completed.
/// </summary>
public sealed class AcpConnectionClosedException : Exception
{
    public AcpConnectionClosedException(string reason, Exception? innerException = null)
        : base($"The ACP connection closed before the operation finished: {reason}", innerException)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
