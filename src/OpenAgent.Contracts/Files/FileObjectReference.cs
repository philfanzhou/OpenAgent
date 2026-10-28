namespace OpenAgent.Contracts.Files;

public sealed class FileObjectReference
{
    public required string ObjectKey { get; init; }
}

public sealed class FileObjectAccessReference
{
    public required string ObjectKey { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
