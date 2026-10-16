namespace OpenAgent.Router.Options;

internal sealed class AgentProviderOptions
{
    internal const string SectionName = "RouterSettings:AgentProviders";

    public string DefaultProviderId { get; set; } = string.Empty;
    public List<AgentProviderDefinition> Providers { get; set; } = [];

    internal static bool IsValid(AgentProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultProviderId)
            || options.Providers.Count == 0)
        {
            return false;
        }

        HashSet<string> providerIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (AgentProviderDefinition provider in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id)
                || string.IsNullOrWhiteSpace(provider.Type)
                || !providerIds.Add(provider.Id))
            {
                return false;
            }
        }

        return providerIds.Contains(options.DefaultProviderId);
    }
}

internal sealed class AgentProviderDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
