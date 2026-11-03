using System.Collections.Generic;

namespace OpenAgent.Hosting.Authentication;

/// <summary>
/// 开发环境内置账号校验。仅用于 Basic 模式下的本地开发预览，
/// 配置 BasicPassword 后禁用这些账号。当前开发账号：admin/admin、test/test。
/// </summary>
internal static class DevelopmentCredentials
{
    private static readonly IReadOnlyDictionary<string, string> Accounts =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["admin"] = "admin",
            ["test"] = "test",
        };

    /// <summary>
    /// 校验用户名/密码是否匹配内置开发账号。
    /// </summary>
    internal static bool IsValid(string username, string password) =>
        !string.IsNullOrWhiteSpace(username)
        && Accounts.TryGetValue(username, out string? expected)
        && string.Equals(expected, password, System.StringComparison.Ordinal);
}
