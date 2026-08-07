using System.Text.Json;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class ChatRequestReader
{
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

            using var reader = new StreamReader(request.Body, leaveOpen: true);
            string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new ParsedChatRequest(string.Empty, null, null);
            }

            (string query, string? conversationId, string? agentId) = ChatRequestParser.Parse(body);
            return new ParsedChatRequest(query, conversationId, agentId);
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }
}
