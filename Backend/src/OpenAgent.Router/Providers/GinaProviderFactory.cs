using OpenAgent.Hosting;

namespace OpenAgent.Router.Providers;

internal sealed class GinaProviderFactory(
    IConfiguration configuration,
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
            logger: logger,
            allowInsecureTls: HttpClientSecurity.AllowInsecureTls(configuration));
}
