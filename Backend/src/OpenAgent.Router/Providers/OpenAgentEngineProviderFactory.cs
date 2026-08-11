using OpenAgent.Authorization;

namespace OpenAgent.Router.Providers;

internal sealed class OpenAgentEngineProviderFactory(
    IRouteTable routeTable,
    IPermissionAuthorizationService authorization,
    IDelegatedAuthorizationIssuer grantIssuer) : IAgentProviderFactory
{
    internal const string ProviderType = "OpenAgentEngine";

    public string Type => ProviderType;

    public IAgentProvider Create(
        string providerId,
        IConfigurationSection settings) =>
        new OpenAgentEngineProvider(
            providerId,
            settings,
            routeTable,
            authorization,
            grantIssuer);
}
