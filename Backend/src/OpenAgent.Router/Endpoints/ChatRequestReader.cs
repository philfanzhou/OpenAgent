using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class ChatRequestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<ParsedChatRequest> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        try
        {
            if (request.HasFormContentType)
            {
                IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                return new ParsedChatRequest(
                    form["message"].FirstOrDefault()
                        ?? form["query"].FirstOrDefault()
                        ?? string.Empty,
                    form["conversationId"].FirstOrDefault(),
                    form["agentId"].FirstOrDefault());
            }

            ChatRequest body = await JsonSerializer.DeserializeAsync<ChatRequest>(
                request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("The chat request body is required.");
            return new ParsedChatRequest(
                body.Message,
                ReadContextString(body.Context, "conversationId"),
                ReadContextString(body.Context, "agentId"));
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static string? ReadContextString(
        IReadOnlyDictionary<string, object>? context,
        string key)
    {
        KeyValuePair<string, object> entry = context?.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? default;
        if (entry.Value == null)
        {
            return null;
        }

        return entry.Value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            string value => value,
            _ => throw new JsonException(
                $"The chat request context property '{key}' must be a string.")
        };
    }
}
