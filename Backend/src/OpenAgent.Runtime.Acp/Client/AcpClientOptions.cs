using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Client;

/// <summary>Connection-level product policy for the client role. Options carry wiring only; behavior lives in code.</summary>
public sealed class AcpClientOptions
{
    /// <summary>Identifies this host to the agent during initialize. Omitted on the wire unless set.</summary>
    public Implementation? ClientInfo { get; init; }

    /// <summary>Local bound for the initialize exchange (SPEC: InitializeTimeout 30s).</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum updates buffered per session for the single consumer of
    /// <see cref="AcpSession.ReadUpdatesAsync"/>, including updates that arrive before a
    /// session/new response binds them. Overflow faults the connection instead of dropping
    /// frames or stalling the reader (SPEC: MaxQueuedUpdates / session = 1024).
    /// </summary>
    public int MaxQueuedUpdates { get; init; } = 1024;

    /// <summary>Engine tuning for the underlying RPC connection; engine defaults apply when null.</summary>
    public AcpRpcOptions? Rpc { get; init; }
}
