namespace OpenAgent.Runtime.Acp.Transport;

public sealed record AcpTransportOptions
{
    public int MaxMessageBytes { get; init; } = 32 * 1024 * 1024;

    public bool LeaveOpen { get; init; }
}
