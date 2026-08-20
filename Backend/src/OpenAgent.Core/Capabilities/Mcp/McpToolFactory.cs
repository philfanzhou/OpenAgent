using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// Connects to configured MCP servers with the official MCP C# SDK and returns
/// the SDK's <see cref="McpClientTool"/> instances directly to MAF.
/// </summary>
internal sealed class McpToolFactory(
    McpTransportFactory transportFactory,
    AgentAuthorizationGate authorization,
    IMcpRegistry registry,
    ILoggerFactory loggerFactory,
    ILogger<McpToolFactory> logger)
{
    internal async Task<McpToolRuntime> CreateAsync(
        string agentId,
        McpConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        var clients = new List<McpClient>();
        var tools = new List<AITool>();
        var approvalTargets = new Dictionary<string, ApprovalTarget>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            IEnumerable<McpServerConfig> servers = config.EnabledServerIds.Count > 0
                ? config.EnabledServerIds.Select(registry.Get).Where(server => server != null).Select(server => server!)
                : config.Servers;
            foreach (McpServerConfig server in servers.Where(server =>
                string.Equals(server.TenantId, user.TenantId, StringComparison.Ordinal)))
            {
                string serverName = string.IsNullOrWhiteSpace(server.Name) ? server.Url : server.Name;
                if (!await authorization.IsAvailableAsync(
                        agentId,
                        AgentResourceType.Mcp,
                        serverName,
                        user,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    IClientTransport transport = transportFactory.Create(server);
                    McpClient? client = null;
                    try
                    {
                        client = await McpClient.CreateAsync(
                            transport,
                            CreateClientOptions(server),
                            loggerFactory,
                            cancellationToken).ConfigureAwait(false);
                        clients.Add(client);

                        IList<McpClientTool> serverTools = await client.ListToolsAsync(
                            options: null,
                            cancellationToken).ConfigureAwait(false);
                        foreach (McpClientTool tool in serverTools)
                        {
                            string resourceId = $"{serverName}/{tool.Name}";
                            if (!await IsToolAvailableAsync(
                                    agentId,
                                    resourceId,
                                    user,
                                    cancellationToken).ConfigureAwait(false))
                            {
                                continue;
                            }

                            string runtimeName = CreateRuntimeName(serverName, tool.Name, names);
                            // WithName/WithDescription are official SDK projections. The
                            // underlying invocation still calls the original MCP tool.
                            AIFunction projected = tool
                                .WithName(runtimeName)
                                .WithDescription($"[MCP:{serverName}] {tool.Description}");
                            tools.Add(ApplyApprovalRequirement(
                                projected,
                                server.RequiresHumanApproval));
                            if (server.RequiresHumanApproval)
                            {
                                approvalTargets.Add(runtimeName, new ApprovalTarget(
                                    AgentResourceType.Mcp,
                                    resourceId,
                                    "invoke"));
                            }
                        }
                    }
                    catch
                    {
                        if (client == null)
                        {
                            await DisposeTransportAsync(transport).ConfigureAwait(false);
                        }
                        throw;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "MCP server unavailable. Server={Server}", serverName);
                }
            }

            return new McpToolRuntime(
                tools.AsReadOnly(),
                clients,
                approvalTargets.AsReadOnly());
        }
        catch
        {
            await DisposeClientsAsync(clients).ConfigureAwait(false);
            throw;
        }
    }

    internal static McpClientOptions CreateClientOptions(McpServerConfig server) => new()
    {
        ClientInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "OpenAgent",
            Version = "1.0.0"
        },
        ProtocolVersion = string.IsNullOrWhiteSpace(server.ProtocolVersion)
            ? null
            : server.ProtocolVersion.Trim(),
        InitializationTimeout = TimeSpan.FromSeconds(30)
    };

    internal static AIFunction ApplyApprovalRequirement(
        AIFunction function,
        bool requiresHumanApproval) =>
        requiresHumanApproval ? new ApprovalRequiredAIFunction(function) : function;

    private async Task<bool> IsToolAvailableAsync(
        string agentId,
        string resourceId,
        IAgentUserContext user,
        CancellationToken cancellationToken) =>
        await authorization.IsAvailableAsync(
            agentId,
            AgentResourceType.Mcp,
            resourceId,
            user,
            cancellationToken).ConfigureAwait(false)
        && await authorization.IsAvailableAsync(
            agentId,
            AgentResourceType.Tool,
            resourceId,
            user,
            cancellationToken).ConfigureAwait(false)
        && await authorization.IsAvailableAsync(
            agentId,
            AgentResourceType.Function,
            resourceId,
            user,
            cancellationToken).ConfigureAwait(false);

    private static string CreateRuntimeName(
        string serverName,
        string toolName,
        ISet<string> names)
    {
        string baseName = $"mcp__{Normalize(serverName)}__{Normalize(toolName)}";
        string runtimeName = baseName;
        for (int suffix = 2; !names.Add(runtimeName); suffix++)
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

    private static async Task DisposeClientsAsync(IEnumerable<McpClient> clients)
    {
        foreach (McpClient client in clients.Reverse())
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeTransportAsync(IClientTransport transport)
    {
        if (transport is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (transport is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal sealed class McpToolRuntime(
    IReadOnlyList<AITool> tools,
    IReadOnlyList<McpClient> clients,
    IReadOnlyDictionary<string, ApprovalTarget>? approvalTargets = null) : IAsyncDisposable
{
    internal static McpToolRuntime Empty { get; } = new([], []);

    internal IReadOnlyList<AITool> Tools { get; } = tools;
    internal IReadOnlyDictionary<string, ApprovalTarget> ApprovalTargets { get; } =
        approvalTargets ?? new Dictionary<string, ApprovalTarget>();

    public async ValueTask DisposeAsync()
    {
        foreach (McpClient client in clients.Reverse())
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
