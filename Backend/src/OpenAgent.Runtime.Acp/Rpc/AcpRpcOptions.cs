namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>Connection-level product policy for the RPC engine. Quota overflow is a bounded local failure, never a hang.</summary>
public sealed class AcpRpcOptions
{
    /// <summary>Maximum outbound requests awaiting a response on one connection. Exhaustion fails the new call locally.</summary>
    public int MaxPendingRequests { get; init; } = 128;

    /// <summary>Maximum concurrently running inbound request handlers. Overflow is answered -32603 instead of queueing unbounded work.</summary>
    public int MaxConcurrentHandlers { get; init; } = 32;

    /// <summary>Maximum inbound notifications buffered for ordered delivery. Overflow faults the connection instead of dropping frames.</summary>
    public int MaxQueuedNotifications { get; init; } = 1024;

    /// <summary>Local bound for one outbound call. Elapsed time behaves like a caller cancellation: the cancel notification is sent and the response grace applies.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>How long a sent request cancellation waits for the peer's final response before the connection closes unconfirmed.</summary>
    public TimeSpan CancelGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Prefix of engine-generated outbound string IDs, keeping inbound and outbound ID tables visibly separate. May be empty for interop probes.</summary>
    public string OutboundIdPrefix { get; init; } = "acp-";

    /// <summary>Optional diagnostic sink for protocol-level events the wire cannot carry. Sink exceptions are swallowed.</summary>
    public Action<AcpRpcDiagnosticEvent>? Diagnostic { get; init; }
}
