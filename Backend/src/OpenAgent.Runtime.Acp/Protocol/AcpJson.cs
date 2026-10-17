using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>Source-generated JSON entry points for the pinned stable protocol.</summary>
public static class AcpJson
{
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>())
        ?? throw new JsonException("A protocol value cannot be null.");
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());
    public static JsonElement ParseElement(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    public static T FromElement<T>(JsonElement value) => value.Deserialize(TypeInfo<T>())
        ?? throw new JsonException("A protocol value cannot be null.");
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());
    internal static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)(AcpJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new NotSupportedException("The type is not part of the pinned protocol context."));
}
