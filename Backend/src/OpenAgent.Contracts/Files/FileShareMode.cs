namespace OpenAgent.Contracts.Files;

public enum FileShareMode
{
    /// <summary>临时分享：短期有效，有效期内不限下载次数。</summary>
    Temporary = 0,

    /// <summary>单次分享：仅允许一次成功下载，超时或下载后立即失效。</summary>
    SingleUse = 1,

    /// <summary>长期分享：较长的有效期，有效期内不限下载次数。</summary>
    LongTerm = 2,

    /// <summary>
    /// 按消费方（audience）默认策略生成的分享：未显式指定 mode 时，
    /// 有效期与下载次数来自 audience 配置预设，实际值以 ExpiresAt/MaxDownloads 为准。
    /// </summary>
    Custom = 3
}

public static class FileShareModeParser
{
    /// <summary>
    /// 严格解析模式名；null/空表示未指定（走 audience 默认策略），无法识别返回 false。
    /// </summary>
    public static bool TryParse(string? value, out FileShareMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":
                mode = FileShareMode.Temporary;
                return false;
            case "temporary":
                mode = FileShareMode.Temporary;
                return true;
            case "singleuse" or "single":
                mode = FileShareMode.SingleUse;
                return true;
            case "longterm" or "long-term" or "long":
                mode = FileShareMode.LongTerm;
                return true;
            default:
                mode = FileShareMode.Temporary;
                return false;
        }
    }
}
