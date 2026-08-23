using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAgent.Core.Approvals;

internal static class ApprovalArgumentRedactor
{
    private static readonly string[] SensitiveNames =
    [
        "password",
        "secret",
        "token",
        "apikey",
        "api_key",
        "authorization",
        "credential",
        "cookie",
        "privatekey",
        "private_key"
    ];

    internal static string SerializeRedacted(IDictionary<string, object?>? arguments) =>
        Serialize(arguments);

    private static string Serialize(IDictionary<string, object?>? arguments)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                arguments ?? new Dictionary<string, object?>());
            JsonNode? node = JsonNode.Parse(json);
            Redact(node);
            return node?.ToJsonString() ?? "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
        catch (NotSupportedException)
        {
            return "{}";
        }
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach ((string name, JsonNode? value) in jsonObject.ToList())
            {
                if (IsSensitive(name))
                {
                    jsonObject[name] = "***";
                }
                else
                {
                    Redact(value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? item in jsonArray)
            {
                Redact(item);
            }
        }
    }

    private static bool IsSensitive(string name)
    {
        string normalized = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return SensitiveNames.Any(candidate =>
            normalized.Contains(
                candidate.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal));
    }
}
