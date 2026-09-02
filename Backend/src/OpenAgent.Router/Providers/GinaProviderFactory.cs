namespace OpenAgent.Router.Providers;

internal sealed class GinaProviderFactory(
    ILoggerFactory loggerFactory) : IAgentProviderFactory
{
    internal const string ProviderType = "Gina";

    public string Type => ProviderType;

    public IAgentProvider Create(
        string providerId,
        IConfigurationSection settings) =>
        new GinaProvider(
            providerId,
            settings,
            logger: loggerFactory.CreateLogger<GinaProvider>());
}
