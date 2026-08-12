namespace OpenAgent.Infrastructure.Entities;

internal sealed class FileAssetEntity
{
    public required string FileId { get; init; }
    public required string TenantId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required string Sha256 { get; init; }
    public required string ObjectKey { get; set; }
    public int Source { get; init; }
    public int State { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
