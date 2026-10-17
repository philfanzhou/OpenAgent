using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol;

internal sealed class RpcIdConverter : JsonConverter<RpcId>
{
    public override bool HandleNull => true;
    public override RpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => RpcId.Null,
        JsonTokenType.String => new(reader.GetString()!),
        JsonTokenType.Number when reader.TryGetInt64(out long number) => new(number),
        _ => throw new JsonException("A request ID must be a string, int64, or null.")
    };
    public override void Write(Utf8JsonWriter writer, RpcId value, JsonSerializerOptions options)
    {
        if (value.IsNull) writer.WriteNullValue();
        else if (value.Kind == JsonValueKind.String) writer.WriteStringValue(value.StringValue);
        else writer.WriteNumberValue(value.NumberValue);
    }
}
