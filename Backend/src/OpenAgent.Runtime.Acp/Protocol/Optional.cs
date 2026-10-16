using System.Text.Json;
using System.Text.Json.Serialization;
namespace OpenAgent.Runtime.Acp.Protocol;

public readonly record struct Optional<T>
{
    public bool IsDefined { get; }
    public T? Value { get; }
    public Optional(T? value) { IsDefined = true; Value = value; }
    public static implicit operator Optional<T>(T? value) => new(value);
}
internal sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
{
    public override bool HandleNull => true;
    public override Optional<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => new(reader.TokenType == JsonTokenType.Null ? default : JsonSerializer.Deserialize(ref reader, AcpJson.TypeInfo<T>()));
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.Value is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value.Value, AcpJson.TypeInfo<T>());
    }
}
