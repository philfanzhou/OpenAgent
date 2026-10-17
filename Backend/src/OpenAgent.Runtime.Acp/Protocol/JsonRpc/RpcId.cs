using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>A JSON-RPC null, string, or signed 64-bit ID. Missing IDs use Optional&lt;RpcId&gt;.</summary>
[JsonConverter(typeof(RpcIdConverter))]
public readonly struct RpcId : IEquatable<RpcId>
{
    private readonly string? _text;
    private readonly long _number;
    public RpcId(string value) { ArgumentNullException.ThrowIfNull(value); _text = value; _number = 0; Kind = JsonValueKind.String; }
    public RpcId(long value) { _text = null; _number = value; Kind = JsonValueKind.Number; }
    public JsonValueKind Kind { get; }
    public bool IsNull => Kind is JsonValueKind.Undefined or JsonValueKind.Null;
    public static RpcId Null => default;
    public string StringValue => Kind == JsonValueKind.String ? _text! : throw new InvalidOperationException("The ID is not a string.");
    public long NumberValue => Kind == JsonValueKind.Number ? _number : throw new InvalidOperationException("The ID is not an integer.");
    public bool Equals(RpcId other) => IsNull && other.IsNull || Kind == other.Kind && (Kind == JsonValueKind.String ? _text == other._text : _number == other._number);
    public override bool Equals(object? obj) => obj is RpcId other && Equals(other);
    public override int GetHashCode() => IsNull ? 0 : HashCode.Combine(Kind, _text, _number);
    public static bool operator ==(RpcId left, RpcId right) => left.Equals(right);
    public static bool operator !=(RpcId left, RpcId right) => !left.Equals(right);
    public static implicit operator RpcId(string value) => new(value);
    public static implicit operator RpcId(long value) => new(value);
}
