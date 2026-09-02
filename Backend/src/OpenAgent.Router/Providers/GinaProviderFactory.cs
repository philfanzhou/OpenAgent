namespace OpenAgent.Router.Providers;

internal sealed class GinaProviderFactory(
    ILogger<GinaProvider> logger) : IAgentProviderFactory
{
    internal const string ProviderType = "Gina";

    public string Type => ProviderType;

    public IAgentProvider Create(
        string providerId,
        IConfigurationSection settings) =>
        new GinaProvider(
            providerId,
            settings,
            logger: logger);
}
