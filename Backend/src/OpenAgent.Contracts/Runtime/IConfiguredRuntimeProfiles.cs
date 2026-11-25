using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Runtime;

public interface IConfiguredRuntimeProfiles
{
    IReadOnlyList<RuntimeProfile> ListForTenant(string tenantId);
}
