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
/// 连接来自 <see cref="McpClientPool"/>：客户端跨轮次复用，本轮只做轻量的
/// ListTools（既取最新工具目录，也作为热连接的健康探测）；坏连接淘汰后立即重连一次。
/// </summary>
internal sealed class McpToolFactory(
    McpClientPool clients,
    AgentAuthorizationGate authorization,
    IMcpRegistry registry,
    ILogger<McpToolFactory> logger)
{
    internal async Task<McpToolRuntime> CreateAsync(
        string agentId,
        McpConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<McpServerConfig> servers = config.EnabledServerIds.Count > 0
            ? config.EnabledServerIds.Select(registry.Get).Where(server => server != null).Select(server => server!)
            : config.Servers;
        // 空 TenantId 的存量 profile 视为全局可用，与 AgentAuthorizationGate 对 LLM profile 的约定一致。
        foreach (McpServerConfig server in servers.Where(server =>
            string.IsNullOrEmpty(server.TenantId)
            || string.Equals(server.TenantId, user.TenantId, StringComparison.Ordinal)))
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
                // 一次获取内含一次坏连接重试：连接刚被淘汰（空闲/失效）后立即重建，
                // 只有时序上紧跟的第二次失败才让本轮跳过该服务器。
                McpClient client = (await clients.AcquireAsync(server, user, cancellationToken).ConfigureAwait(false)).Client;
                IList<McpClientTool> serverTools;
                try
                {
                    serverTools = await client.ListToolsAsync(
                        options: null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 池化连接可能已被服务端单方面断开：淘汰缓存并重连一次。
                    logger.LogWarning(exception, "MCP client appears broken, reconnecting. Server={Server}", serverName);
                    await clients.InvalidateAsync(server, user).ConfigureAwait(false);
                    client = (await clients.AcquireAsync(server, user, cancellationToken).ConfigureAwait(false)).Client;
                    serverTools = await client.ListToolsAsync(
                        options: null,
                        cancellationToken).ConfigureAwait(false);
                }

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
                    tools.Add(tool
                        .WithName(runtimeName)
                        .WithDescription($"[MCP:{serverName}] {tool.Description}"));
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

        // 客户端由池持有，运行时只携带工具清单；作用域释放不再断开连接。
        return new McpToolRuntime(tools.AsReadOnly());
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

}

internal sealed class McpToolRuntime(
    IReadOnlyList<AITool> tools) : IAsyncDisposable
{
    internal static McpToolRuntime Empty { get; } = new([]);

    internal IReadOnlyList<AITool> Tools { get; } = tools;

    // 连接由 McpClientPool 持有并跨轮次复用；运行时本身没有需要释放的资源。
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
