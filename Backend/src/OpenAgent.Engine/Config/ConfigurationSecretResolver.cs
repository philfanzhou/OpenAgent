using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Config;

/// <summary>
/// Resolves tenant-scoped secrets from the host configuration pipeline.
/// Production deployments can replace this registration with a Vault or
/// cloud secret-manager adapter without changing persisted Agent documents.
/// </summary>
internal sealed class ConfigurationSecretResolver(IConfiguration configuration)
    : IAgentSecretResolver
{
    public Task<string?> ResolveAsync(
        string tenantId,
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(tenantId, nameof(tenantId));
        ValidateReference(secretReference);
        string? secret = configuration[$"Secrets:{tenantId}:{secretReference}"];
        return Task.FromResult(string.IsNullOrWhiteSpace(secret) ? null : secret);
    }

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
