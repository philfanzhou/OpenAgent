namespace OpenAgent.Contracts.Files;

public sealed class FileObjectWriteRequest
{
    public required string FileId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public FileObjectScope Scope { get; init; } = FileObjectScope.User;
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required string Sha256 { get; init; }
    public string? ObjectKeyPrefix { get; init; }
}
