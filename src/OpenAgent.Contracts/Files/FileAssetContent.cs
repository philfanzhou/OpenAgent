namespace OpenAgent.Contracts.Files;

public sealed class FileAssetContent
{
    public required FileAsset Asset { get; init; }
    public required byte[] Data { get; init; }
}
