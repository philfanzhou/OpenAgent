using Microsoft.Extensions.Options;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Providers;

internal sealed class InternDecisionProviderFactory(
    ILogger<InternDecisionProvider> logger,
    IOptions<IntentRecognitionOptions> options) : IAgentProviderFactory
{
    public string Type => Options.InternDecisionOptions.ProviderType;

    public IAgentProvider Create(string providerId, IConfigurationSection settings) =>
        new InternDecisionProvider(providerId, settings, logger, timeoutMs: options.Value.TimeoutMs);
}
