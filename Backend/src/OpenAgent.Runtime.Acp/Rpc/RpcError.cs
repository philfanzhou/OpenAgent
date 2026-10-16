using System.Text.Json;
namespace OpenAgent.Runtime.Acp.Rpc;
public sealed record RpcError(int Code, string Message, JsonElement? Data = null);
public sealed class AcpRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement? ErrorData { get; } = data?.Clone();
}
internal sealed record WireMessage(string Jsonrpc = "2.0", JsonElement? Id = null, string? Method = null,
    JsonElement? Params = null, JsonElement? Result = null, RpcError? Error = null);
