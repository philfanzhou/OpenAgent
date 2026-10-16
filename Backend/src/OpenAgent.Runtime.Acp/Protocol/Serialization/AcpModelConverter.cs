using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol.Serialization;

internal abstract class AcpModelConverter<T>(string schemaName, Func<JsonElement, T> create) : JsonConverter<T> where T : AcpModel
{
    public override bool HandleNull => true;
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return create(SchemaValidator.Process(schemaName, document.RootElement, compatibility: true));
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("A protocol model cannot be null.");
        SchemaValidator.Process(schemaName, value.Json, compatibility: false).WriteTo(writer);
    }
}
