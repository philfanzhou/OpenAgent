using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using System.Text.Json.Serialization;

namespace OpenAgent.Engine.Config;

internal sealed class AgentListQuery(
    IRedisConnectionProvider redis,
    ILogger<AgentListQuery> logger,
    AgentConfigLocalStore localStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(CancellationToken cancellationToken)
    {
        return await ExecuteCoreAsync(tenantId: null, user: null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        string tenantId = user.TenantId
            ?? throw new TenantDataIsolationException(null, null, "TenantId is required but not provided");
        return await ExecuteCoreAsync(tenantId, user, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentSummary>> ExecuteCoreAsync(
        string? tenantId,
        IAgentUserContext? user,
        CancellationToken cancellationToken)
    {
        var result = new List<AgentSummary>();
        AddLocalAgents(result, tenantId, user);
        if (!redis.IsAvailable)
        {
            EngineLog.ListAgentsRedisUnavailable(logger);
            return result;
        }

        try
        {
            var agentIds = await redis.SetMembersAsync("agent:published:index").ConfigureAwait(false);
            foreach (var agentId in agentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configJson = await redis.StringGetAsync($"agent:config:{agentId}").ConfigureAwait(false);
                if (configJson.IsNullOrEmpty)
                {
                    continue;
                }

                try
                {
                    var entity = JsonSerializer.Deserialize<AgentConfigEntity>(configJson.ToString(), JsonOptions);
                    if (entity != null)
                    {
                        AddAgent(result, entity, tenantId, user);
                    }
                }
                catch (Exception exception)
                {
                    EngineLog.ListAgentsParseFailed(logger, exception, agentId, configJson.ToString().Length);
                }
            }

        }
        catch (Exception exception)
        {
            EngineLog.ListAgentsFailed(logger, exception);
        }

        return result;
    }

    private void AddLocalAgents(List<AgentSummary> result, string? tenantId, IAgentUserContext? user)
    {
        foreach (AgentConfigEntity entity in localStore.List())
        {
            AddAgent(result, entity, tenantId, user);
        }
    }

    private static void AddAgent(
        List<AgentSummary> result,
        AgentConfigEntity entity,
        string? tenantId,
        IAgentUserContext? user)
    {
        string ownerTenantId = string.IsNullOrWhiteSpace(entity.TenantId)
            ? entity.Config?.TenantId ?? string.Empty
            : entity.TenantId;
        // 调试用：空租户（存量/未迁移）数据视为全局可见，不再被租户过滤。
        if (tenantId != null
            && !string.IsNullOrWhiteSpace(ownerTenantId)
            && !string.Equals(ownerTenantId, tenantId, StringComparison.Ordinal))
        {
            return;
        }

        if (user != null && entity.Config != null && !AgentAccess.IsAllowed(entity.Config, user))
        {
            return;
        }

        result.RemoveAll(item => string.Equals(item.AgentId, entity.AgentId, StringComparison.OrdinalIgnoreCase));
        result.Add(new AgentSummary
        {
            AgentId = entity.AgentId,
            Name = entity.Name,
            Description = entity.Description,
            Status = (int)entity.Status,
            CurrentVersion = entity.CurrentVersion,
            ApiFormat = entity.Config?.Llm?.Format.ToString() ?? "unknown",
            LlmProvider = entity.Config?.Llm?.Provider ?? string.Empty,
            LlmModel = entity.Config?.Llm?.ModelId ?? string.Empty
        });
    }
}
