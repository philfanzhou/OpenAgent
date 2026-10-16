using System.Text.Json;

namespace OpenAgent.Acp.Demo;

/// <summary>One outgoing request at a time, while still dispatching notifications and reverse requests.</summary>
internal sealed class ClientConnection(JsonLineTransport transport)
{
    internal async Task<JsonElement> RequestAsync<T>(string id, string method, T parameters,
        Func<JsonElement, Task> onNotification, CancellationToken cancellationToken = default)
    {
        await transport.WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            byte[] bytes = await transport.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Agent disconnected before replying.");
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("jsonrpc", out JsonElement version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            {
                throw new InvalidDataException("Invalid JSON-RPC envelope received from agent.");
            }

            if (message.TryGetProperty("method", out JsonElement incomingMethod))
            {
                if (incomingMethod.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Invalid JSON-RPC method received from agent.");
                }

                if (message.TryGetProperty("id", out JsonElement requestId))
                {
                    // No client capabilities are advertised; never implicitly grant permissions.
                    if (incomingMethod.GetString() == "session/request_permission")
                    {
                        await transport.WriteAsync(new
                        {
                            jsonrpc = "2.0", id = requestId,
                            result = new { outcome = new { outcome = "cancelled" } }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await transport.WriteAsync(new
                        {
                            jsonrpc = "2.0", id = requestId, error = new { code = -32601, message = "Method not found" }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await onNotification(message.Clone()).ConfigureAwait(false);
                }

                continue;
            }

            if (!message.TryGetProperty("id", out JsonElement responseId) || responseId.ValueKind != JsonValueKind.String ||
                responseId.GetString() != id)
            {
                throw new InvalidDataException("Unexpected JSON-RPC response ID.");
            }

            if (message.TryGetProperty("error", out JsonElement error))
            {
                throw new RpcException(error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString()!);
            }

            return message.GetProperty("result").Clone();
        }
    }
}
