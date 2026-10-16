using System.Text.Json;
using System.Text.Json.Serialization;
namespace OpenAgent.Runtime.Acp.Protocol;

public readonly record struct Optional<T>
{
    public bool IsDefined { get; }
    public bool IsNull { get; }
    public T? Value { get; }
    public Optional(T? value) { IsDefined = true; IsNull = value is null; Value = value; }
    private Optional(bool isNull) { IsDefined = true; IsNull = isNull; Value = default; }
    // JsonElement 等值类型也需要显式 null，不能将它与 default/省略混淆。
    public static Optional<T> Null => new(isNull: true);
    public static implicit operator Optional<T>(T? value) => new(value);
}
internal sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
{
    public override bool HandleNull => true;
    public override Optional<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? Optional<T>.Null : new(JsonSerializer.Deserialize(ref reader, AcpJson.TypeInfo<T>()));
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.IsNull || value.Value is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value.Value, AcpJson.TypeInfo<T>());
    }
}
