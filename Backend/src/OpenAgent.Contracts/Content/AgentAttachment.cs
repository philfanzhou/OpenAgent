namespace OpenAgent.Contracts.Content;

public sealed class AgentAttachment
{
    public string? FileId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Data { get; init; }

    public long Length => Data.LongLength;
}
