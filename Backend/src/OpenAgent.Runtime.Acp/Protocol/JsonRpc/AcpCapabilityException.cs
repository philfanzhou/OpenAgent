namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>
/// A capability or protocol-version gate rejected the operation locally, before anything
/// reached the wire (PROTOCOL §2: send-side validation throws instead of trial-and-error).
/// </summary>
public sealed class AcpCapabilityException : Exception
{
    public AcpCapabilityException(string message) : base(message) { }
}
