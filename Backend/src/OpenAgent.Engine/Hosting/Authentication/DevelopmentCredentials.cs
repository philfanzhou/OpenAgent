using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Engine.Hosting.Authentication;

/// <summary>
/// Basic credential validation with development-account compatibility.
/// Configured passwords disable the built-in development accounts.
/// </summary>
internal static class DevelopmentCredentials
{
    private static readonly IReadOnlyDictionary<string, string> Accounts =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["admin"] = "admin",
            ["test"] = "test",
        };

    internal static bool IsValid(
        string username,
        string password,
        AgentAuthenticationOptions options,
        bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(options.BasicPassword))
        {
            return isDevelopment && IsValid(username, password);
        }

        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.BasicPassword));
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(expected, supplied)
            && string.Equals(username, options.BasicUsername, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 校验用户名/密码是否匹配内置开发账号。
    /// </summary>
    internal static bool IsValid(string username, string password) =>
        !string.IsNullOrWhiteSpace(username)
        && Accounts.TryGetValue(username, out string? expected)
        && string.Equals(expected, password, System.StringComparison.Ordinal);
}
