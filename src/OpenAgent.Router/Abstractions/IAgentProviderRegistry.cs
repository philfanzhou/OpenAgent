namespace OpenAgent.Router;

internal interface IAgentProviderRegistry
{
    IReadOnlyList<IAgentProvider> Providers { get; }

    IAgentProvider DefaultProvider { get; }

    bool TryGet(string providerId, out IAgentProvider? provider);
}
