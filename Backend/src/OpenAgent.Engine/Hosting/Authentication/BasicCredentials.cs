using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Engine.Hosting.Authentication;

/// <summary>
/// Basic credential validation for the built-in accounts (admin / test),
/// available in any environment. A configured BasicPassword overrides the
/// built-in passwords of both accounts.
/// </summary>
internal static class BasicCredentials
{
    private static readonly IReadOnlyDictionary<string, string> Accounts =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["admin"] = "admin",
            ["test"] = "test",
        };

    /// <summary>
    /// 校验用户名是否为内置账号、密码是否匹配；设置 BasicPassword 后双账号共用该密码。
    /// </summary>
    internal static bool IsValid(string username, string password, AgentAuthenticationOptions options)
    {
        if (string.IsNullOrWhiteSpace(username)
            || !Accounts.TryGetValue(username, out string? builtInPassword))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.BasicPassword))
        {
            return string.Equals(builtInPassword, password, System.StringComparison.Ordinal);
        }

        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.BasicPassword));
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
