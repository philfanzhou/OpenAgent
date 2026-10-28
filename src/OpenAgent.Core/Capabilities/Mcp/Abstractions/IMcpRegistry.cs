using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Abstract;

public interface IMcpRegistry
{
    IReadOnlyList<McpServerConfig> GetAll();
    McpServerConfig? Get(string id);
    void Register(McpServerConfig server);
    bool Remove(string id);
}
