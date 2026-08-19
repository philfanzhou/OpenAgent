using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpRegistry : IMcpRegistry
{
    private readonly Dictionary<string, McpServerConfig> _servers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<McpServerConfig> GetAll() => [.. _servers.Values];

    public McpServerConfig? Get(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _servers.GetValueOrDefault(id);

    public void Register(McpServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.Name))
            _servers[server.Name] = server;
    }

    public bool Remove(string id) => _servers.Remove(id);
}
