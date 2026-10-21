using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Capabilities.Security;
using OpenAgent.Application.Tooling.Abstractions;

namespace OpenAgent.Application.Capabilities.Mcp;

/// <summary>
/// 按 Agent 配置解析启用中的外部 MCP server 定义，交由运行时驱动经 ACP
/// session/new 透传给 harness 原生连接。平台不再代理连接外部 server：
/// 逐调用的工具授权/预算只在平台自有工具上生效，透传 server 的凭据与
/// 限流由 harness 直接持有（见 docs/integrations/opencode-runtime.md）。
/// 租户注册表（IMcpConfigRepository）是唯一定义来源；内联 Servers 仅为
/// 存量 Agent 配置保留。
/// </summary>
internal sealed class McpFeatureFactory(
    AgentAuthorizationGate authorization,
    IMcpConfigRepository? repository = null) : IAgentFeatureFactory
{
    public async Task<AgentFeature> CreateAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        List<RuntimeMcpServer> servers = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<Contracts.Configuration.McpServerConfig> configured;
        if (repository is not null && !string.IsNullOrWhiteSpace(user.TenantId) && config.Mcp.EnabledServerIds.Count > 0)
        {
            List<Contracts.Configuration.McpServerConfig> persisted = [];
            foreach (string id in config.Mcp.EnabledServerIds)
            {
                McpServerConfig? saved = await repository.GetAsync(user.TenantId, id, cancellationToken).ConfigureAwait(false);
                if (saved is not null) persisted.Add(saved);
            }
            configured = persisted;
        }
        else
        {
            // 无租户上下文或无仓储时无法解析 EnabledServerIds，退化为内联存量配置。
            configured = config.Mcp.Servers;
        }
        // 空 TenantId 的存量 profile 视为全局可用，与 AgentAuthorizationGate 对 LLM profile 的约定一致。
        foreach (Contracts.Configuration.McpServerConfig server in configured.Where(server =>
            string.IsNullOrEmpty(server.TenantId)
            || string.Equals(server.TenantId, user.TenantId, StringComparison.Ordinal)))
        {
            string name = string.IsNullOrWhiteSpace(server.Name) ? server.Url : server.Name;
            if (!seen.Add(name))
            {
                continue;
            }
            if (!await authorization.IsAvailableAsync(
                    agentId,
                    AgentResourceType.Mcp,
                    name,
                    user,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            servers.Add(new RuntimeMcpServer(
                name,
                server.Url,
                server.Type == McpServerType.SSE ? "sse" : "http"));
        }
        return new AgentFeature { McpServers = servers };
    }
}
