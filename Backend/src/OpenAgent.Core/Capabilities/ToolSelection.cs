namespace OpenAgent.Core.Capabilities;

/// <summary>
/// 每代理工具禁用规则的匹配：精确名或前缀通配（"mcp__server__*" 禁整个服务器）。
/// 大小写不敏感（MCP 运行时名已小写化，内置名小写）。
/// </summary>
internal static class ToolSelection
{
    internal static bool IsDisabled(IReadOnlyList<string> patterns, string name)
    {
        foreach (string raw in patterns)
        {
            string pattern = raw.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }
            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                string prefix = pattern[..^1];
                if (prefix.Length > 0
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
