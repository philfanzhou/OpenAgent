using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Runtime.Acp.Protocol.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>An immutable, owned protocol value. Unknown standard object properties are discarded on read.</summary>
public abstract class AcpModel
{
    private protected AcpModel(string schemaName, JsonElement value, bool validated)
    {
        SchemaName = schemaName;
        Json = validated ? value.Clone() : SchemaValidator.Process(schemaName, value, compatibility: false);
    }

    [JsonIgnore]
    public string SchemaName { get; }
    /// <summary>Owned JSON, valid independently of the input document's lifetime.</summary>
    [JsonIgnore]
    public JsonElement Json { get; }

    private protected T Get<T>(string name) => AcpJson.FromElement<T>(Json.GetProperty(name));
    private protected Optional<T> GetOptional<T>(string name)
    {
        if (!Json.TryGetProperty(name, out JsonElement value)) return default;
        return value.ValueKind == JsonValueKind.Null ? Optional<T>.Null : Optional<T>.FromValue(AcpJson.FromElement<T>(value));
    }

    private protected static JsonElement Build(Action<Utf8JsonWriter> action)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) action(writer);
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private protected static void Write<T>(Utf8JsonWriter writer, string name, T value)
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, AcpJson.TypeInfo<T>());
    }

    private protected static void WriteOptional<T>(Utf8JsonWriter writer, string name, Optional<T> value)
    {
        if (!value.IsDefined) return;
        if (value.IsNull) writer.WriteNull(name);
        else Write(writer, name, value.Value);
    }
}
