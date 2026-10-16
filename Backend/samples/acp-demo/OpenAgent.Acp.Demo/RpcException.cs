namespace OpenAgent.Acp.Demo;

internal sealed class RpcException(int code, string message) : Exception(message)
{
    internal int Code { get; } = code;
}
