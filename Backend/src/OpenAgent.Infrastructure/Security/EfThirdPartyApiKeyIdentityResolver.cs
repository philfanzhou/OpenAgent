using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Security;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure.Security;

public sealed class EfThirdPartyApiKeyIdentityResolver(
    IDbContextFactory<OpenAgentDbContext> contexts)
    : IThirdPartyApiKeyIdentityResolver
{
    public async Task<ThirdPartyApiKeyIdentity?> ResolveAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        string keyHash = ThirdPartyApiKeyHashing.Compute(apiKey);
        await using OpenAgentDbContext database = await contexts
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        ThirdPartyApiKeyEntity? credential = await database.ThirdPartyApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.KeyHash == keyHash
                    && item.IsEnabled
                    && (item.ExpiresAt == null || item.ExpiresAt > DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        if (credential == null)
        {
            return null;
        }

        return new ThirdPartyApiKeyIdentity
        {
            UserId = credential.UserId,
            Username = credential.Username,
            Email = credential.Email,
            TenantId = credential.TenantId,
            Roles = Split(credential.Roles),
            Groups = Split(credential.Groups),
            Claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scope"] = credential.Scopes
            },
            Audience = ["openagent-api"]
        };
    }

    private static IReadOnlyList<string> Split(string value) => value
        .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
