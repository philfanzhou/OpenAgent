using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpCapabilitySource(
    McpClientPool clients,
    AgentAuthorizationGate authorization,
    ILogger<McpCapabilitySource> logger) : ICapabilitySource
{
    public async Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        await clients.ResetAsync(cancellationToken).ConfigureAwait(false);
        List<CapabilityDefinition> result = [];
        foreach (McpServerConfig server in config.Mcp.Servers)
        {
            McpServerIdentity serverIdentity = McpServerIdentity.From(server);
            if (!await authorization.IsAuthorizedAsync(
                agentId,
                AgentResourceType.Mcp,
                serverIdentity.Name,
                "discover",
                user,
                cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                IMcpClient client = await clients.GetConnectedClientAsync(
                    server,
                    cancellationToken).ConfigureAwait(false);
                IReadOnlyList<McpTool> tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                foreach (McpTool tool in tools)
                {
                    McpToolIdentity binding = clients.RegisterTool(server, tool.Name);
                    result.Add(new CapabilityDefinition(
                        binding.RuntimeName,
                        $"[MCP:{binding.Server.Name}] {tool.Description}",
                        tool.Schema ?? "{\"type\":\"object\"}",
                        AgentResourceType.Mcp,
                        binding.ResourceId,
                        (arguments, invocationCancellation) => clients.CallToolAsync(
                            binding,
                            arguments.ToDictionary(item => item.Key, item => item.Value ?? string.Empty),
                            invocationCancellation),
                        binding.Server.Name));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                McpLog.DiscoveryFailed(logger, exception, serverIdentity.Name);
            }
        }

        return result.AsReadOnly();
    }
}
