using Microsoft.Extensions.Options;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Routing;

internal sealed class AgentProviderRegistry : IAgentProviderRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;

    public AgentProviderRegistry(
        IEnumerable<IAgentProviderFactory> factories,
        IOptions<AgentProviderOptions> options,
        IConfiguration configuration)
    {
        Dictionary<string, IAgentProviderFactory> factoriesByType = factories.ToDictionary(
            factory => factory.Type,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IConfigurationSection> settingsById = configuration
            .GetSection(AgentProviderOptions.SectionName)
            .GetSection("Providers")
            .GetChildren()
            .Where(section => !string.IsNullOrWhiteSpace(section["Id"]))
            .ToDictionary(
                section => section["Id"]!,
                section => section.GetSection("Settings"),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, IAgentProvider> providers = new(StringComparer.OrdinalIgnoreCase);
        foreach (AgentProviderDefinition definition in options.Value.Providers)
        {
            if (!factoriesByType.TryGetValue(definition.Type, out IAgentProviderFactory? factory))
            {
                throw new InvalidOperationException(
                    $"Agent provider type '{definition.Type}' is not registered.");
            }

            if (!settingsById.TryGetValue(definition.Id, out IConfigurationSection? settings))
            {
                throw new InvalidOperationException(
                    $"Agent provider '{definition.Id}' has no configuration section.");
            }

            IAgentProvider provider = factory.Create(
                definition.Id,
                settings);
            if (!string.Equals(provider.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Agent provider '{definition.Id}' returned a different provider ID.");
            }

            providers.Add(definition.Id, provider);
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
