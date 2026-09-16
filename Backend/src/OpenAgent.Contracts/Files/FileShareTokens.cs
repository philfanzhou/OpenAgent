using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Contracts.Files;

/// <summary>
/// 分享令牌生成与哈希。令牌是 128 位随机 GUID 的十六进制表示，仅在创建响应中出现；
/// 持久层只保存其 SHA-256 哈希，与第三方 API Key 的存储约定一致。
/// </summary>
public static class FileShareTokens
{
    public static string NewToken() => Guid.NewGuid().ToString("N");

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
