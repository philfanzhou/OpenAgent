using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>An inbound request handed to a registered handler. Parameters are the raw, already schema-validated payload.</summary>
public sealed class AcpRpcRequest
{
    internal AcpRpcRequest(RpcId id, string method, JsonElement? parameters)
    {
        Id = id;
        Method = method;
        Parameters = parameters;
    }

    /// <summary>The peer's request ID; echo it verbatim by returning normally or by throwing <see cref="AcpRpcException"/>.</summary>
    public RpcId Id { get; }

    public string Method { get; }

    /// <summary>The raw parameters object, or null when the envelope omitted params.</summary>
    public JsonElement? Parameters { get; }
}

/// <summary>An inbound notification handed to a registered handler. Notifications receive no response.</summary>
public sealed class AcpRpcNotification
{
    internal AcpRpcNotification(string method, JsonElement? parameters)
    {
        Method = method;
        Parameters = parameters;
    }

    public string Method { get; }

    /// <summary>The raw parameters object, or null when the envelope omitted params.</summary>
    public JsonElement? Parameters { get; }
}
