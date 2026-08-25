namespace OpenAgent.Contracts.Files;

/// <summary>
/// One entry of a compress_files request. Exactly one of <see cref="FileId"/>
/// or <see cref="ObjectKey"/> must be provided.
/// </summary>
public sealed class FileArchiveItem
{
    public string? FileId { get; init; }
    public string? ObjectKey { get; init; }

    /// <summary>Zip entry name; may contain relative folders such as "images/a.png".</summary>
    public string? FileName { get; init; }
}

public sealed class FileArchiveRequest
{
    public required string OutputName { get; init; }
    public required IReadOnlyList<FileArchiveItem> Items { get; init; }
}

public sealed class FileArchiveResult
{
    public required string ObjectKey { get; init; }
    public required long Length { get; init; }
    public required int FileCount { get; init; }
}
