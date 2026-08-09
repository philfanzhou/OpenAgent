using System.Text.Json;

namespace OpenAgent.Router.Endpoints;

internal static class ChatRequestParser
{
    internal static (string Query, string? ConversationId, string? AgentId) Parse(string body)
    {
        using var json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The chat request root must be a JSON object.");
        }

        string query = ReadOptionalString(root, "query")
            ?? ReadOptionalString(root, "message")
            ?? string.Empty;
        string? conversationId = null;
        string? agentId = null;
        if (TryGetProperty(root, "context", out JsonElement context))
        {
            if (context.ValueKind == JsonValueKind.Object)
            {
                conversationId = ReadOptionalString(context, "conversationId");
                agentId = ReadOptionalString(context, "agentId");
            }
            else if (context.ValueKind != JsonValueKind.Null)
            {
                throw new JsonException("The chat request context must be a JSON object.");
            }
        }

        conversationId ??= ReadOptionalString(root, "conversationId");
        agentId ??= ReadOptionalString(root, "agentId");

        return (query, conversationId, agentId);
    }

    private static string? ReadOptionalString(JsonElement value, string name)
    {
        if (!TryGetProperty(value, name, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw new JsonException($"The chat request property '{name}' must be a string.");
    }

    private static bool TryGetProperty(
        JsonElement value,
        string name,
        out JsonElement property)
    {
        if (value.TryGetProperty(name, out property))
        {
            return true;
        }

        foreach (JsonProperty candidate in value.EnumerateObject())
        {
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}
