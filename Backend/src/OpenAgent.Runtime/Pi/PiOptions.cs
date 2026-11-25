using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime.Pi;

internal sealed class PiOptions
{
    public bool Enabled { get; set; }
    public string Executable { get; set; } = string.Empty;
    public string[] AllowedTenants { get; set; } = [];
    public string[] AllowedNativeTools { get; set; } = [];
}

internal sealed class PiProfiles(PiOptions options) : IConfiguredRuntimeProfiles
{
    internal const string ProfileId = "openagent-pi";

    public IReadOnlyList<RuntimeProfile> ListForTenant(string tenantId) =>
        options.Enabled && options.AllowedTenants.Contains(tenantId, StringComparer.Ordinal)
            ? [new RuntimeProfile { Id = ProfileId, Name = "Pi", Provider = "Pi" }]
            : [];
}
