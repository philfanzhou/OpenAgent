namespace OpenAgent.Runtime.Acp.Client;

/// <summary>
/// The session already has an active prompt turn. This is a local SDK policy (one turn per
/// session at a time); it fails before anything reaches the wire.
/// </summary>
public sealed class SessionBusyException : Exception
{
    public SessionBusyException(string sessionId)
        : base($"The session '{sessionId}' already has an active prompt turn; the SDK allows one turn per session at a time.")
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
}
