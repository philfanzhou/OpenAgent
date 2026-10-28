namespace OpenAgent.Contracts.Files;

public sealed class FileAssetScope
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? ConversationId { get; init; }
}
