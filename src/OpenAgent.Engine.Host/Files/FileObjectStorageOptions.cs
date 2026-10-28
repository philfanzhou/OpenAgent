namespace OpenAgent.Engine.Host.Files;

internal sealed class FileObjectStorageOptions
{
    internal const string SectionName = "FileAssets:ObjectStorage";

    public string BucketName { get; init; } = "openagent-files";
    public string KeyPrefix { get; init; } = "files";
    public string Region { get; init; } = "us-east-1";
    public string? ServiceUrl { get; init; }
    public bool ForcePathStyle { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
}
