namespace OpenAgent.Contracts.Files;

public sealed class FileAssetCreateRequest
{
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required FileAssetSource Source { get; init; }
}
