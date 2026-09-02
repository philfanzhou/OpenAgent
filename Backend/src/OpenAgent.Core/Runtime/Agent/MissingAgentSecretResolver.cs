using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class MissingAgentSecretResolver : IAgentSecretResolver
{
    public Task<string?> ResolveAsync(
        string tenantId,
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
