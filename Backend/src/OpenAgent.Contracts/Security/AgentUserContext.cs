using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Contracts.Security;

public sealed class ThirdPartyApiKeyIdentity
{
    public required string UserId { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }
    public required string TenantId { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyDictionary<string, string> Claims { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<string> Audience { get; init; } = [];
}

public interface IThirdPartyApiKeyIdentityResolver
{
    Task<ThirdPartyApiKeyIdentity?> ResolveAsync(
        string apiKey,
        CancellationToken cancellationToken = default);
}

public static class ThirdPartyApiKeyHashing
{
    public static string Compute(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}

public interface IAgentUserContext
{
    string UserId { get; }
    string? Username { get; }
    string? Email { get; }
    string? TenantId { get; }
    IReadOnlyList<string> Groups { get; }
    IReadOnlyList<string> Roles { get; }
    IReadOnlyDictionary<string, string> Claims { get; }
    IReadOnlyList<string> Audience { get; }
    bool IsAuthenticated { get; }
}

public class AgentUserContext : IAgentUserContext
{
    public required string UserId { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Audience { get; init; } = [];
    public bool IsAuthenticated { get; init; } = true;
}
