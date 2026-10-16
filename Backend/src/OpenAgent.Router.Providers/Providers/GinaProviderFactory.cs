namespace OpenAgent.Router.Providers;

internal sealed class GinaProviderFactory : IAgentProviderFactory
{
    internal const string ProviderType = "Gina";

    public string Type => ProviderType;

    public IAgentProvider Create(
        string providerId,
        IConfigurationSection settings) =>
        new GinaProvider(providerId, settings);
}
