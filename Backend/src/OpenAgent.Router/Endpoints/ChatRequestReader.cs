using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Router.Middleware;
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

            if (RequestBodySnapshot.TryGet(request.HttpContext, out Task<RequestBodySnapshot> snapshotTask))
            {
                RequestBodySnapshot snapshot = await snapshotTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Digest.Length > 0)
                {
                    ChatRequest cachedBody = JsonSerializer.Deserialize<ChatRequest>(
                        snapshot.CanonicalBody,
                        JsonOptions) ?? throw new JsonException(
                            "The chat request body is required.");
                    return ToParsedRequest(cachedBody);
                }
            }

            ChatRequest body = await JsonSerializer.DeserializeAsync<ChatRequest>(
                request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("The chat request body is required.");
            return ToParsedRequest(body);
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static ParsedChatRequest ToParsedRequest(ChatRequest body)
    {
        return new ParsedChatRequest(
            body.Message,
            ReadContextString(body.Context, "conversationId"),
            ReadContextString(body.Context, "agentId"));
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
