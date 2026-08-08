namespace OpenAgent.Contracts.Content;

public sealed class AgentAttachment
{
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Data { get; init; }
    public string? ObjectKey { get; init; }
    public string? Sha256 { get; init; }

    public long Length => Data.LongLength;
}
