using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpCapabilitySource(
    IMcpClientFactory factory,
    AgentAuthorizationGate authorization,
    ILogger<McpCapabilitySource> logger) : ICapabilitySource, IAsyncDisposable
{
    private readonly Dictionary<McpServerIdentity, IMcpClient> _clients = new();
    private readonly HashSet<string> _runtimeNames = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        _runtimeNames.Clear();
        List<CapabilityDefinition> result = [];
        foreach (McpServerConfig server in config.Mcp.Servers)
        {
            McpServerIdentity identity = McpServerIdentity.From(server);
            if (!await authorization.IsAvailableAsync(
                agentId,
                AgentResourceType.Mcp,
                identity.Name,
                user,
                cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                IMcpClient client = await GetClientAsync(server, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<McpTool> tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                foreach (McpTool tool in tools)
                {
                    string runtimeName = CreateRuntimeName(identity.Name, tool.Name);
                    result.Add(new CapabilityDefinition(
                        runtimeName,
                        $"[MCP:{identity.Name}] {tool.Description}",
                        tool.Schema ?? "{\"type\":\"object\"}",
                        AgentResourceType.Mcp,
                        $"{identity.Name}/{tool.Name}",
                        (arguments, invocationCancellation) => client.CallToolAsync(
                            tool.Name,
                            arguments.ToDictionary(
                                item => item.Key,
                                item => item.Value ?? string.Empty),
                            invocationCancellation),
                        identity.Name));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                McpLog.DiscoveryFailed(logger, exception, identity.Name);
            }
        }

        return result.AsReadOnly();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IMcpClient client in _clients.Values)
        {
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else if (client.IsConnected)
            {
                await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        _clients.Clear();
        _runtimeNames.Clear();
    }

    private async Task<IMcpClient> GetClientAsync(
        McpServerConfig server,
        CancellationToken cancellationToken)
    {
        McpServerIdentity identity = McpServerIdentity.From(server);
        if (!_clients.TryGetValue(identity, out IMcpClient? client))
        {
            client = factory.Create();
            _clients.Add(identity, client);
        }

        if (!client.IsConnected)
        {
            await client.ConnectAsync(identity.Url, identity.Type, cancellationToken).ConfigureAwait(false);
        }

        return client;
    }

    private string CreateRuntimeName(string serverName, string toolName)
    {
        string baseName = $"mcp__{Normalize(serverName)}__{Normalize(toolName)}";
        string runtimeName = baseName;
        for (int suffix = 2; !_runtimeNames.Add(runtimeName); suffix++)
        {
            runtimeName = $"{baseName}__{suffix}";
        }

        return runtimeName;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
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
