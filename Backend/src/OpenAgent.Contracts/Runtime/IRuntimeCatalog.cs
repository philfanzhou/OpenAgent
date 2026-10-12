namespace OpenAgent.Contracts.Runtime;

public interface IRuntimeCatalog
{
    IReadOnlyList<RuntimeDescriptor> List();
}
