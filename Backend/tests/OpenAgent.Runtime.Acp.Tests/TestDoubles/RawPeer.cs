using System.Text;
using System.Text.Json;

namespace OpenAgent.Runtime.Acp.Tests.TestDoubles;

/// <summary>
/// Drives one loopback side with raw protocol frames, so role-layer tests observe the actual
/// wire instead of trusting the peer role implementation.
/// </summary>
public sealed class RawPeer
{
    private readonly LoopbackTransport _wire;

    public RawPeer(LoopbackTransport wire) => _wire = wire;

    /// <summary>The wire this peer drives; its tap carries every frame the role engine wrote.</summary>
    public LoopbackTransport Wire => _wire;

    public Task WriteAsync(string json) => _wire.WriteAsync(Encoding.UTF8.GetBytes(json));

    public void SimulateEof() => _wire.SimulateEof();

    public async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
    {
        // The peer consumes the inbound channel, exactly like the engine it stands in for.
        byte[]? frame = await _wire.ReadAsync(cancellationToken)
            ?? throw new InvalidOperationException("The wire reached EOF before the expected frame.");
        return JsonDocument.Parse(frame);
    }

    /// <summary>Reads frames until one carries the given id and a result or error.</summary>
    public async Task<JsonDocument> ReceiveResponseAsync(string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonDocument frame = await ReceiveAsync(cancellationToken);
            if (frame.RootElement.TryGetProperty("id", out JsonElement frameId) && frameId.GetString() == id)
            {
                return frame;
            }
        }
    }

    /// <summary>Reads frames until the peer issues the named request, exposing its wire id for the reply.</summary>
    public async Task<JsonDocument> ReceiveRequestAsync(string method, CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonDocument frame = await ReceiveAsync(cancellationToken);
            if (frame.RootElement.TryGetProperty("method", out JsonElement frameMethod) && frameMethod.GetString() == method)
            {
                return frame;
            }
        }
    }

    public async Task<JsonDocument> ReceiveNotificationAsync(string method, CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonDocument frame = await ReceiveAsync(cancellationToken);
            if (frame.RootElement.TryGetProperty("method", out JsonElement frameMethod) && frameMethod.GetString() == method)
            {
                return frame;
            }
        }
    }

    /// <summary>Answers the given request frame with a result payload built inline.</summary>
    public Task AnswerAsync(JsonDocument request, string resultJson)
    {
        string id = request.RootElement.GetProperty("id").GetRawText();
        return WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}");
    }

    /// <summary>Answers the given request frame with an error object.</summary>
    public Task FailAsync(JsonDocument request, int code, string message)
    {
        string id = request.RootElement.GetProperty("id").GetRawText();
        return WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"message\":\"" + message + "\"}}");
    }
}
