using System.Text.Json;
namespace OpenAgent.Runtime.Acp.Client;

// ACP 表单只允许基础字段；保留 schema 为 JSON，但校验提交值，避免引入完整 JSON Schema 引擎。
internal static class ElicitationForm
{
    internal static void Validate(JsonElement schema, JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Object } value || !schema.TryGetProperty("properties", out JsonElement properties)) throw new ArgumentException("Invalid form content.");
        if (schema.TryGetProperty("required", out JsonElement required))
            foreach (JsonElement name in required.EnumerateArray()) if (!value.TryGetProperty(name.GetString()!, out _)) throw new ArgumentException("Missing form field.");
        foreach (JsonProperty item in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(item.Name, out JsonElement field) || !Matches(field, item.Value)) throw new ArgumentException("Invalid form field.");
        }
    }
    private static bool Matches(JsonElement field, JsonElement value)
    {
        string? type = field.GetProperty("type").GetString();
        bool valid = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && (!field.TryGetProperty("minLength", out JsonElement min) || value.GetString()!.EnumerateRunes().Count() >= min.GetInt32()) && (!field.TryGetProperty("maxLength", out JsonElement max) || value.GetString()!.EnumerateRunes().Count() <= max.GetInt32()),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" or "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && (type != "integer" || decimal.Truncate(number) == number) && (!field.TryGetProperty("minimum", out JsonElement min) || number >= min.GetDecimal()) && (!field.TryGetProperty("maximum", out JsonElement max) || number <= max.GetDecimal()),
            "array" => value.ValueKind == JsonValueKind.Array && field.TryGetProperty("items", out JsonElement items) && value.EnumerateArray().All(x => Matches(items, x)) && (!field.TryGetProperty("minItems", out JsonElement min) || value.GetArrayLength() >= min.GetInt32()) && (!field.TryGetProperty("maxItems", out JsonElement max) || value.GetArrayLength() <= max.GetInt32()),
            _ => false
        };
        return valid && (!field.TryGetProperty("enum", out JsonElement choices) || choices.EnumerateArray().Any(x => JsonElement.DeepEquals(x, value)));
    }
}
