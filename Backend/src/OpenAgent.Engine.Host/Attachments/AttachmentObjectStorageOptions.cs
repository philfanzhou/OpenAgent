namespace OpenAgent.Engine.Host.Attachments;

internal sealed class AttachmentObjectStorageOptions
{
    internal const string SectionName = "Attachments:ObjectStorage";

    public bool Enabled { get; init; }
    public string BucketName { get; init; } = "openagent-attachments";
    public string KeyPrefix { get; init; } = "attachments";
    public string Region { get; init; } = "us-east-1";
    public string? ServiceUrl { get; init; }
    public bool ForcePathStyle { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
}
