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
                || !IsValidBaseUrl(provider.BaseUrl)
                || !providerIds.Add(provider.Id))
            {
                return false;
            }
        }

        return providerIds.Contains(options.DefaultProviderId);
    }

    // An empty BaseUrl means "discover the instance through the route table".
    private static bool IsValidBaseUrl(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
        || (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}

internal sealed class AgentProviderDefinition
{
    public string Id { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public Dictionary<string, string> ServiceHeaders { get; set; } = [];
}
