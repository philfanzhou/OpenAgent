namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>A remote RPC error, distinct from local JSON, transport, and capability failures.</summary>
public sealed class AcpRpcException(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
    public int Code => Error.Code;
}
