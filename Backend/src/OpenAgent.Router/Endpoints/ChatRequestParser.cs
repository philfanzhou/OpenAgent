using System.Text.Json;

namespace OpenAgent.Router.Endpoints;

internal static class ChatRequestParser
{
    internal static (string Query, string? ConversationId, string? AgentId) Parse(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var query = root.TryGetProperty("query", out var queryElement)
            ? queryElement.GetString() ?? string.Empty
            : root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;
        string? conversationId = null;
        string? agentId = null;
        if (root.TryGetProperty("context", out var context)
            && context.ValueKind == JsonValueKind.Object)
        {
            if (context.TryGetProperty("conversationId", out var contextConversation))
            {
                conversationId = contextConversation.GetString();
            }

            if (context.TryGetProperty("agentId", out var contextAgent))
            {
                agentId = contextAgent.GetString();
            }
        }

        if (conversationId == null && root.TryGetProperty("conversationId", out var directConversation))
        {
            conversationId = directConversation.GetString();
        }

        if (agentId == null && root.TryGetProperty("agentId", out var directAgent))
        {
            agentId = directAgent.GetString();
        }

        return (query, conversationId, agentId);
    }
}
