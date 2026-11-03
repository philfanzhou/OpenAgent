using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Hosting.Authentication;

internal static class BasicCredentials
{
    internal static bool IsValid(
        string username,
        string password,
        AgentAuthenticationOptions options,
        bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(options.BasicPassword))
        {
            return isDevelopment && DevelopmentCredentials.IsValid(username, password);
        }

        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.BasicPassword));
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(expected, supplied)
            && string.Equals(username, options.BasicUsername, StringComparison.Ordinal);
    }
}
