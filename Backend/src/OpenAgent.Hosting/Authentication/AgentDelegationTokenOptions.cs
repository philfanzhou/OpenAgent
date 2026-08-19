namespace OpenAgent.Hosting.Authentication;

public sealed class AgentDelegationTokenOptions
{
    public const string SectionName = "Authentication:ProviderToken";

    public string Issuer { get; set; } = "openagent-router";

    public string Audience { get; set; } = "openagent-engine-provider";

    public string SigningKey { get; set; } = string.Empty;

    public int LifetimeSeconds { get; set; } = 60;
}
