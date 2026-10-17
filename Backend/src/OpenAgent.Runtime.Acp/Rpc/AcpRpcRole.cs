namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>Which side of the ACP link one engine instance plays; it fixes the wire direction of every envelope.</summary>
public enum AcpRpcRole
{
    /// <summary>Sends client_to_agent messages and receives agent_to_client messages.</summary>
    Client,

    /// <summary>Sends agent_to_client messages and receives client_to_agent messages.</summary>
    Agent
}
