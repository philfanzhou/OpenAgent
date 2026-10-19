using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>
/// One prompt turn handed to the host's prompt handler. The handler sends updates and reverse
/// requests through the context, returns the stop reason on natural completion, and lets its
/// cancellation token fire when the peer cancels — the host then answers cancelled.
/// </summary>
public sealed class AcpPromptContext
{
    internal AcpPromptContext(AcpAgentConnection connection, string sessionId, ContentBlock[] prompt)
    {
        Connection = connection;
        SessionId = sessionId;
        Prompt = prompt;
    }

    public AcpAgentConnection Connection { get; }

    public string SessionId { get; }

    public IReadOnlyList<ContentBlock> Prompt { get; }

    /// <summary>Sends a session update on the wire; the write is ordered against the turn's final response.</summary>
    public Task SendUpdateAsync(SessionUpdate update, CancellationToken cancellationToken = default) =>
        Connection.SendUpdateAsync(SessionId, update, cancellationToken);

    /// <summary>Asks the client to approve one tool call. The outcome is selected or cancelled; nothing is auto-approved.</summary>
    public async Task<RequestPermissionOutcome> RequestPermissionAsync(
        ToolCallUpdate toolCall,
        IReadOnlyList<PermissionOption> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(options);
        RequestPermissionRequest request = new(SessionId, toolCall, [.. options]);
        RequestPermissionResponse response = await Connection.SendRequestAsync<RequestPermissionRequest, RequestPermissionResponse>(
            "session/request_permission", request, cancellationToken).ConfigureAwait(false);
        return response.Outcome;
    }
}
