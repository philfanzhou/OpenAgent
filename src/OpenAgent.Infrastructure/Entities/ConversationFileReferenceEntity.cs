namespace OpenAgent.Infrastructure.Entities;

internal sealed class ConversationFileReferenceEntity
{
    public required string ConversationId { get; init; }
    public required string FileId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
