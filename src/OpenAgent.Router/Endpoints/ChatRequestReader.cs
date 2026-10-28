using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Hosting;
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
                    form[ChatRequestContext.ConversationIdKey].FirstOrDefault(),
                    form[ChatRequestContext.AgentIdKey].FirstOrDefault(),
                    form[ChatRequestContext.LlmProfileIdKey].FirstOrDefault());
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
            ChatRequestContext.ReadString(body.Context, ChatRequestContext.ConversationIdKey),
            ChatRequestContext.ReadString(body.Context, ChatRequestContext.AgentIdKey),
            ChatRequestContext.ReadString(body.Context, ChatRequestContext.LlmProfileIdKey));
    }
}
