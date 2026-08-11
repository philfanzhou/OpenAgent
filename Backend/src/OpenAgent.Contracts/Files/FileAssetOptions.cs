namespace OpenAgent.Contracts.Files;

public sealed class FileAssetOptions
{
    public const string SectionName = "FileAssets";

    public bool Enabled { get; init; }
    public string? MetadataConnectionString { get; init; }
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    public long MaxFunctionReadBytes { get; init; } = 128 * 1024;
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
    [".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".json", ".txt", ".csv", ".md"];
}
