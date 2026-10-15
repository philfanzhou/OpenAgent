using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Capabilities;

public interface IMcpRegistry
{
    IReadOnlyList<McpServerConfig> GetAll();
    McpServerConfig? Get(string id);
    void Register(McpServerConfig server);
    bool Remove(string id);
}
