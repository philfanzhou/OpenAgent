using System.Text;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpClientPool(IMcpClientFactory factory) : IAsyncDisposable
{
    private readonly Dictionary<McpServerIdentity, IMcpClient> _clients = new();
    private readonly Dictionary<string, McpToolIdentity> _tools = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<IMcpClient> GetConnectedClientAsync(
        McpServerConfig server,
        CancellationToken cancellationToken)
    {
        var identity = McpServerIdentity.From(server);
        var created = false;
        if (!_clients.TryGetValue(identity, out var client))
        {
            client = factory.Create();
            _clients.Add(identity, client);
            created = true;
        }

        // Each server identity owns an independent logical connection.  The
        // production factory creates a separate client per identity; keeping
        // the initial connect explicit also makes a shared fake client obey
        // the same identity-switching contract in tests.
        if (created || !client.IsConnected)
        {
            await client.ConnectAsync(identity.Url, identity.Type, cancellationToken).ConfigureAwait(false);
        }

        return client;
    }

    internal McpToolIdentity RegisterTool(McpServerConfig server, string toolName)
    {
        var serverIdentity = McpServerIdentity.From(server);
        var baseName = $"mcp__{Normalize(serverIdentity.Name)}__{Normalize(toolName)}";
        var runtimeName = baseName;
        var suffix = 1;
        while (_tools.ContainsKey(runtimeName))
        {
            suffix++;
            runtimeName = $"{baseName}__{suffix}";
        }

        var identity = new McpToolIdentity(
            runtimeName,
            toolName,
            $"{serverIdentity.Name}/{toolName}",
            serverIdentity);
        _tools.Add(runtimeName, identity);
        return identity;
    }

    internal bool TryGetTool(string runtimeName, out McpToolIdentity identity) =>
        _tools.TryGetValue(runtimeName, out identity);

    internal async Task<string> CallToolAsync(
        McpToolIdentity tool,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(tool.Server, out var client))
        {
            throw new InvalidOperationException($"MCP client is not available for server '{tool.Server.Name}'.");
        }

        return await client.CallToolAsync(tool.DisplayName, arguments, cancellationToken).ConfigureAwait(false)
            ?? "No result from MCP tool";
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        _tools.Clear();
        var clients = new HashSet<IMcpClient>(_clients.Values, ReferenceEqualityComparer.Instance);
        foreach (var client in clients)
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _clients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        return builder.ToString().Trim('_');
    }
}

internal readonly record struct McpServerIdentity(string Name, string Url, McpServerType Type)
{
    internal static McpServerIdentity From(McpServerConfig server) => new(
        string.IsNullOrWhiteSpace(server.Name) ? server.Url : server.Name,
        server.Url,
        server.Type);
}

internal readonly record struct McpToolIdentity(
    string RuntimeName,
    string DisplayName,
    string ResourceId,
    McpServerIdentity Server);
