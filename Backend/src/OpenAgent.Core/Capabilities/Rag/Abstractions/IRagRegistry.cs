using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Abstract;

public interface IRagRegistry
{
    List<RagInstanceConfig> GetAllInstances();

    RagInstanceConfig? GetInstance(string id);

    void Register(RagInstanceConfig instance);
}
