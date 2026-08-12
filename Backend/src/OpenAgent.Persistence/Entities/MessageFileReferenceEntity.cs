namespace OpenAgent.Persistence.Entities;

internal sealed class MessageFileReferenceEntity
{
    public required string MessageId { get; init; }
    public required string FileId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
