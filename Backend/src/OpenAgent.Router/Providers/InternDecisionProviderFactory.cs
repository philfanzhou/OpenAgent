using Microsoft.Extensions.Logging;

namespace OpenAgent.Router.Providers;

internal sealed class InternDecisionProviderFactory(
    ILogger<InternDecisionProvider> logger) : IAgentProviderFactory
{
    public string Type => Options.InternDecisionOptions.ProviderType;

    public IAgentProvider Create(string providerId, IConfigurationSection settings) =>
        new InternDecisionProvider(providerId, settings, logger);
}