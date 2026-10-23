namespace OpenAgent.Contracts.Files;

/// <summary>
/// 文件扩展名与媒体类型的唯一事实源，仅做类型推断，不做准入限制：
/// 媒体类型推断（缺省或通用类型按扩展名取规范类型）、download_file 的无扩展名补全、
/// 对象下发的 Content-Type 与 read_file 的文本判定全部由此派生。
/// 目录之外的扩展名不拒绝入库，一律按调用方声明类型（缺省 application/octet-stream）存储。
/// 新增可识别类型只需在此追加一条记录。
/// </summary>
public static class FileMediaTypeCatalog
{
    /// <summary>扩展名 → 规范媒体类型，Aliases 为同扩展名额外可接受的等价类型（如浏览器上报的 .md=text/plain）。</summary>
    private sealed record Specification(string Extension, string MediaType, string[] Aliases);

    private static readonly Specification[] Specifications =
    [
        new(".png", "image/png", []),
        new(".jpg", "image/jpeg", []),
        new(".jpeg", "image/jpeg", []),
        new(".jps", "image/jpeg", []),
        new(".gif", "image/gif", []),
        new(".webp", "image/webp", []),
        new(".svg", "image/svg+xml", []),
        new(".pdf", "application/pdf", []),
        new(".json", "application/json", ["text/json"]),
        new(".xml", "application/xml", ["text/xml"]),
        new(".drawio", "application/vnd.jgraph.mxfile", []),
        new(".txt", "text/plain", []),
        new(".csv", "text/csv", []),
        new(".md", "text/markdown", ["text/plain"]),
        new(".html", "text/html", []),
        new(".htm", "text/html", []),
        new(".css", "text/css", []),
        new(".zip", "application/zip", []),
        new(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", []),
        new(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [])
    ];

    private static readonly IReadOnlyDictionary<string, Specification> ByExtension =
        Specifications.ToDictionary(spec => spec.Extension, spec => spec, StringComparer.OrdinalIgnoreCase);

    /// <summary>反向映射只取规范类型：多个扩展名共享同一规范类型时以首个为准，别名不参与反查。</summary>
    private static readonly IReadOnlyDictionary<string, string> ExtensionByMediaType =
        Specifications
            .GroupBy(spec => spec.MediaType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Extension, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AllExtensions { get; } =
        Specifications.Select(spec => spec.Extension).ToArray();

    public static IReadOnlyList<string> AllMediaTypes { get; } =
        Specifications
            .SelectMany(spec => spec.Aliases.Prepend(spec.MediaType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool TryGetMediaType(string extension, out string mediaType)
    {
        if (ByExtension.TryGetValue(extension, out Specification? spec))
        {
            mediaType = spec.MediaType;
            return true;
        }
        mediaType = string.Empty;
        return false;
    }

    /// <summary>按规范媒体类型反查扩展名，用于为无扩展名文件补后缀。</summary>
    public static bool TryGetExtensionForMediaType(string mediaType, out string extension)
    {
        if (ExtensionByMediaType.TryGetValue(mediaType, out string? matched))
        {
            extension = matched;
            return true;
        }
        extension = string.Empty;
        return false;
    }

    private static readonly HashSet<string> NonTextualTextMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/xml",
        "image/svg+xml",
        "application/vnd.jgraph.mxfile"
    };

    /// <summary>read_file 等文本读取路径的判定：所有 text/* 及以文本存储的结构化类型。</summary>
    public static bool IsTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || NonTextualTextMediaTypes.Contains(mediaType);
}
