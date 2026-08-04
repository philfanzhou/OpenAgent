using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Capabilities.Rag;

internal class RagRegistry : IRagRegistry
{
    private readonly Dictionary<string, RagInstanceConfig> _instances = new(StringComparer.OrdinalIgnoreCase);

    public void Register(RagInstanceConfig instance)
    {
        if (string.IsNullOrEmpty(instance.Id)) return;
        _instances[instance.Id] = instance;
    }

    public List<RagInstanceConfig> GetAllInstances()
    {
        return _instances.Values.ToList();
    }

    public RagInstanceConfig? GetInstance(string id)
    {
        return _instances.GetValueOrDefault(id);
    }
}
