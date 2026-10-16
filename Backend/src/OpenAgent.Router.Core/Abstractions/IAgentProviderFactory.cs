using Microsoft.Extensions.Configuration;
namespace OpenAgent.Router;

public interface IAgentProviderFactory
{
    string Type { get; }

    IAgentProvider Create(
        string providerId,
        IConfigurationSection settings);
}
