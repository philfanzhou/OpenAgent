using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>
/// The peer never answered a cancelled request within the grace period. The outcome of the
/// remote work is unknown, so the connection closes; no synthetic cancelled result is reported.
/// </summary>
public sealed class AcpCancellationUnconfirmedException : Exception
{
    public AcpCancellationUnconfirmedException(string method, RpcId id)
        : base($"The '{method}' request was cancelled but the peer sent no final response within the grace period.")
    {
        Method = method;
        Id = id;
    }

    public string Method { get; }

    public RpcId Id { get; }
}
