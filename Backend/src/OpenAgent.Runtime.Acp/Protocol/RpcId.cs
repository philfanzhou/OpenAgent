using System.Text.Json;
using System.Text.Json.Serialization;
namespace OpenAgent.Runtime.Acp.Protocol;

[JsonConverter(typeof(RpcIdConverter))]
public readonly record struct RpcId
{
    private readonly string? _json;
    public RpcId(string value) { ArgumentNullException.ThrowIfNull(value); _json = JsonSerializer.Serialize(value, AcpJsonContext.Default.String); }
    public RpcId(long value) => _json = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static RpcId Null => default;
    public override string ToString() => _json ?? "null";
    internal JsonElement Element => AcpJson.Parse(ToString());
}
internal sealed class RpcIdConverter : JsonConverter<RpcId>
{
    public override bool HandleNull => true;
    public override RpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => RpcId.Null,
        JsonTokenType.String => new(reader.GetString()!),
        JsonTokenType.Number when reader.TryGetInt64(out long value) => new(value),
        _ => throw new JsonException("Invalid JSON-RPC id.")
    };
    public override void Write(Utf8JsonWriter writer, RpcId value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToString());
}
