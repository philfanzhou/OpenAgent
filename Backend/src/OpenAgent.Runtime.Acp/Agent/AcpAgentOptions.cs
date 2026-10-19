using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>Connection-level product policy for the agent host. Options carry wiring only.</summary>
public sealed class AcpAgentOptions
{
    /// <summary>Maximum sessions tracked on one connection (SPEC: MaxSessions / connection = 32). Overflow answers the SDK resource-limit error.</summary>
    public int MaxSessions { get; init; } = 32;

    /// <summary>Engine tuning for the underlying RPC connection; engine defaults apply when null.</summary>
    public AcpRpcOptions? Rpc { get; init; }
}
