namespace OpenAgent.Contracts.Files;

public sealed class FileAssetOptions
{
    public const string SectionName = "FileAssets";

    public bool Enabled { get; init; }
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    public long MaxFunctionReadBytes { get; init; } = 128 * 1024;
    public long MaxInlineImageBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxInlineImageCount { get; init; } = 4;
    public long MaxArchiveInputBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxArchiveFileCount { get; init; } = 100;
    public IReadOnlyList<string> AllowedMediaTypes { get; init; } =
    [
        "image/*",
        "application/pdf",
        "application/json",
        "application/zip",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/plain",
        "text/csv",
        "text/markdown"
    ];
    public IReadOnlyList<string> AllowedExtensions { get; init; } =
    [".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".json", ".txt", ".csv", ".md", ".zip", ".pptx", ".xlsx"];
}
