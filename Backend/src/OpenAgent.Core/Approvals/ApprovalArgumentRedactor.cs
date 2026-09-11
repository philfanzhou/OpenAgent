using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAgent.Core.Approvals;

internal static class ApprovalArgumentRedactor
{
    private static readonly string[] SensitiveNames = ["key", "secret", "token", "password", "credential", "authorization"];

    internal static string Serialize(object? arguments)
    {
        JsonNode? node = JsonNode.Parse(JsonSerializer.Serialize(arguments));
        Redact(node);
        return node?.ToJsonString() ?? "{}";
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (KeyValuePair<string, JsonNode?> property in objectNode.ToList())
            {
                if (SensitiveNames.Any(name => property.Key.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    objectNode[property.Key] = "[redacted]";
                }
                else
                {
                    Redact(property.Value);
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (JsonNode? item in arrayNode)
            {
                Redact(item);
            }
        }
    }
}
