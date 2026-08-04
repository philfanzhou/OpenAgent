namespace OpenAgent.Engine.Host.Attachments;

internal sealed class AgentAttachmentOptions
{
    internal const string SectionName = "Attachments";

    public int MaxFileCount { get; init; } = 5;
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    public long MaxTotalSizeBytes { get; init; } = 25 * 1024 * 1024;
    public IReadOnlyList<string> AllowedMediaTypes { get; init; } =
    [
        "image/*",
        "application/pdf",
        "application/json",
        "text/plain",
        "text/csv",
        "text/markdown"
    ];
    public IReadOnlyList<string> AllowedExtensions { get; init; } =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".pdf",
        ".json",
        ".txt",
        ".csv",
        ".md"
    ];
}
