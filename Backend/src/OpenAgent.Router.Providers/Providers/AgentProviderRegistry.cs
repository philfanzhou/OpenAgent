using Microsoft.Extensions.Options;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Providers;

internal sealed class AgentProviderRegistry : IAgentProviderRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;

    public AgentProviderRegistry(
        IOptions<AgentProviderOptions> options,
        IRouteTable routeTable)
    {
        Dictionary<string, IAgentProvider> providers = new(StringComparer.OrdinalIgnoreCase);
        foreach (AgentProviderDefinition definition in options.Value.Providers)
        {
            providers.Add(definition.Id, new HttpProvider(definition, routeTable));
        }

        _providers = providers;
        Providers = providers.Values.ToArray();
        DefaultProvider = providers[options.Value.DefaultProviderId];
    }

    public IReadOnlyList<IAgentProvider> Providers { get; }

    public IAgentProvider DefaultProvider { get; }

    public bool TryGet(string providerId, out IAgentProvider? provider) =>
        _providers.TryGetValue(providerId, out provider);

    public void Dispose()
    {
        foreach (IDisposable provider in Providers.OfType<IDisposable>())
        {
            provider.Dispose();
        }
    }
}
