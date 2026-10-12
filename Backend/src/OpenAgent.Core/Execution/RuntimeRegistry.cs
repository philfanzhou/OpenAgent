using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Core.Execution;

internal sealed class RuntimeRegistry(IEnumerable<IRuntimeAdapter> adapters) : IRuntimeCatalog
{
    private readonly IReadOnlyDictionary<string, IRuntimeAdapter> _adapters = adapters.ToDictionary(adapter => adapter.Id, StringComparer.Ordinal);
    public IReadOnlyList<RuntimeDescriptor> List() => _adapters.Values.SelectMany(adapter => adapter.Describe()).ToArray();

    internal IRuntimeAdapter Resolve(RuntimeBinding binding)
    {
        if (!_adapters.TryGetValue(binding.Adapter, out IRuntimeAdapter? adapter)
            || !adapter.Describe().Any(item => item.Harness == binding.Harness))
            throw new AgentException(AgentErrorCode.InvalidRequest, "The configured runtime or harness is not available.");
        return adapter;
    }
}
