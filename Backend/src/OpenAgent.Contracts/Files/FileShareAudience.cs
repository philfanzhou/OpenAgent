namespace OpenAgent.Contracts.Files;

/// <summary>
/// 分享链接的消费方。不同消费方有不同的默认策略：
/// MCP（交给第三方工具，暴露面大）默认短有效期、限次下载；User（交给最终用户）默认较长有效期、不限次。
/// 显式指定 <see cref="FileShareMode"/> 时以 mode 为准，audience 默认被覆盖。
/// </summary>
public enum FileShareAudience
{
    /// <summary>最终用户下载/分享；默认 3 天、不限次数。</summary>
    User = 0,

    /// <summary>第三方 MCP 工具拉取；默认 2 小时、最多 2 次下载。</summary>
    Mcp = 1
}

public static class FileShareAudienceParser
{
    /// <summary>严格解析消费方名称；null/空/无法识别返回 false，由调用方决定默认值。</summary>
    public static bool TryParse(string? value, out FileShareAudience audience)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "user":
                audience = FileShareAudience.User;
                return true;
            case "mcp":
                audience = FileShareAudience.Mcp;
                return true;
            default:
                audience = FileShareAudience.User;
                return false;
        }
    }
}
