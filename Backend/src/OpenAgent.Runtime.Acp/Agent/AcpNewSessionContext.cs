using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>One session/new handed to the host's new-session handler. The id is already allocated.</summary>
public sealed class AcpNewSessionContext
{
    internal AcpNewSessionContext(AcpAgentConnection connection, string sessionId, string cwd, McpServer[] mcpServers)
    {
        Connection = connection;
        SessionId = sessionId;
        Cwd = cwd;
        McpServers = mcpServers;
    }

    public AcpAgentConnection Connection { get; }

    public string SessionId { get; }

    public string Cwd { get; }

    public McpServer[] McpServers { get; }

    /// <summary>
    /// Sends a session update for this session. Updates sent before the handler returns leave
    /// on the wire before the session/new response — the client binds them on arrival.
    /// </summary>
    public Task SendUpdateAsync(SessionUpdate update, CancellationToken cancellationToken = default) =>
        Connection.SendUpdateAsync(SessionId, update, cancellationToken);
}
