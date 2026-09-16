namespace OpenAgent.Contracts.Files;

public enum FileShareMode
{
    /// <summary>临时分享：短期有效，有效期内不限下载次数。</summary>
    Temporary = 0,

    /// <summary>单次分享：仅允许一次成功下载，超时或下载后立即失效。</summary>
    SingleUse = 1,

    /// <summary>长期分享：较长的有效期，有效期内不限下载次数。</summary>
    LongTerm = 2
}

public static class FileShareModeParser
{
    /// <summary>宽松解析模式名；null/空视为默认 Temporary，无法识别时返回 null。</summary>
    public static FileShareMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "temporary" => FileShareMode.Temporary,
        "singleuse" or "single" => FileShareMode.SingleUse,
        "longterm" or "long-term" or "long" => FileShareMode.LongTerm,
        _ => null
    };
}
