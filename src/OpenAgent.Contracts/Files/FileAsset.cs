namespace OpenAgent.Contracts.Files;

public sealed class FileAsset
{
    public required string FileId { get; init; }
    public required string TenantId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public required string ObjectKey { get; init; }
    public required FileAssetSource Source { get; init; }
    public required FileAssetState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
