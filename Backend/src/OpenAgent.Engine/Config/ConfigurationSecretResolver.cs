using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Config;

/// <summary>
/// Resolves tenant-scoped secrets from the host configuration pipeline.
/// Production deployments can replace this registration with a Vault or
/// cloud secret-manager adapter without changing persisted Agent documents.
/// </summary>
internal sealed class ConfigurationSecretResolver : IAgentSecretResolver
{
    private const string Prefix = "v1.";
    private readonly IConfiguration _configuration;
    private readonly IDataProtectionProvider _provider;

    public ConfigurationSecretResolver(
        IConfiguration configuration,
        IDataProtectionProvider? provider = null)
    {
        _configuration = configuration;
        _provider = provider ?? new EphemeralDataProtectionProvider();
    }

    public Task<string> ProtectAsync(
        string tenantId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(tenantId, nameof(tenantId));
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return Task.FromResult(Prefix + Protector(tenantId).Protect(secret));
    }

    public Task<string?> ResolveAsync(
        string tenantId,
        string protectedSecret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(tenantId, nameof(tenantId));
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return Task.FromResult<string?>(null);
        }

        if (protectedSecret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                return Task.FromResult<string?>(Protector(tenantId).Unprotect(protectedSecret[Prefix.Length..]));
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                return Task.FromResult<string?>(null);
            }
        }

        string? legacy = null;
        try
        {
            ValidateReference(protectedSecret);
            legacy = _configuration[$"Secrets:{tenantId}:{protectedSecret}"];
        }
        catch (ArgumentException)
        {
            return Task.FromResult<string?>(protectedSecret);
        }
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            return Task.FromResult<string?>(legacy);
        }

        // Accept legacy plaintext rows once so they can be re-encrypted on the next write.
        return Task.FromResult<string?>(protectedSecret.Contains(':', StringComparison.Ordinal)
            ? null
            : protectedSecret);
    }

    private IDataProtector Protector(string tenantId) =>
        _provider.CreateProtector("OpenAgent", "AgentSecrets", tenantId);

    private static void ValidateReference(string secretReference)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            throw new ArgumentException("Secret reference is required.", nameof(secretReference));
        }

        foreach (string segment in secretReference.Split(':'))
        {
            ValidateSegment(segment, nameof(secretReference));
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(character => !char.IsLetterOrDigit(character)
                && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "Secret path segments may contain only letters, digits, '-', '_' and '.'.",
                parameterName);
        }
    }
}
