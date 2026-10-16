namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>Distinguishes an omitted property, explicit JSON null, and a value.</summary>
public readonly struct Optional<T>
{
    private readonly byte _state;
    private readonly T? _value;
    private Optional(byte state, T? value) { _state = state; _value = value; }
    public bool IsDefined => _state != 0;
    public bool IsNull => _state == 1;
    public bool HasValue => _state == 2;
    public T Value => HasValue ? _value! : throw new InvalidOperationException("The property has no value.");
    public static Optional<T> Null => new(1, default);
    public static Optional<T> FromValue(T value) => value is null ? Null : new(2, value);
    public static implicit operator Optional<T>(T value) => FromValue(value);
}
