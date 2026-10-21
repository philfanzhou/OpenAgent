using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Engine.Config;

/// <summary>
/// 租户 MCP 注册表的管理入口：PostgreSQL（IMcpConfigRepository）是唯一定义来源。
/// 透传给 OpenCode 的运行时读取走 McpFeatureFactory，与本服务共用同一仓储。
/// </summary>
public sealed class McpProfileManagementService(IMcpConfigRepository repository)
{
    internal async Task<IReadOnlyList<McpServerConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<McpServerConfig> servers = await repository.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        // 空 TenantId 的存量 server 视为全局可见。
        return servers
            .Where(server => string.IsNullOrWhiteSpace(server.TenantId)
                || string.Equals(server.TenantId, tenantId, StringComparison.Ordinal))
            .ToArray();
    }

    internal async Task<McpServerConfig?> GetAsync(
        string id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        McpServerConfig? server = await repository.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        return server != null
            && (string.IsNullOrWhiteSpace(server.TenantId)
                || string.Equals(server.TenantId, tenantId, StringComparison.Ordinal))
            ? server
            : null;
    }

    internal async Task<McpServerConfig> SaveAsync(
        McpServerConfig server,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        server.TenantId = tenantId;
        await repository.SaveAsync(tenantId, server, cancellationToken).ConfigureAwait(false);
        return server;
    }

    internal Task<bool> DeleteAsync(
        string id,
        string tenantId,
        CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(tenantId, id, cancellationToken);
}
