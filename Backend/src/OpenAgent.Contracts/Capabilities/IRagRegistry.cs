using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Capabilities;

public interface IRagRegistry
{
    List<RagInstanceConfig> GetAllInstances();

    RagInstanceConfig? GetInstance(string id);

    void Register(RagInstanceConfig instance);
}
