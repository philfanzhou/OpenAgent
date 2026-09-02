using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Config;

internal sealed class AgentListQuery(
    IRedisConnectionProvider redis,
    ILogger<AgentListQuery> logger,
    AgentConfigLocalStore localStore,
    AgentConfigDatabaseStore? databaseStore = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(CancellationToken cancellationToken)
    {
        return await ExecuteCoreAsync(tenantId: null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        return await ExecuteCoreAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentSummary>> ExecuteCoreAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (databaseStore != null)
        {
            IReadOnlyList<AgentConfigEntity> databaseAgents = await databaseStore
                .ListAuthoritativeAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            var databaseResult = new List<AgentSummary>(databaseAgents.Count);
            foreach (AgentConfigEntity entity in databaseAgents)
            {
                AddAgent(databaseResult, entity, tenantId);
            }
            return databaseResult;
        }

        var result = new List<AgentSummary>();
        AddLocalAgents(result, tenantId);
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
                        AddAgent(result, entity, tenantId);
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

    private void AddLocalAgents(List<AgentSummary> result, string? tenantId)
    {
        foreach (AgentConfigEntity entity in localStore.List())
        {
            AddAgent(result, entity, tenantId);
        }
    }

    private static void AddAgent(
        List<AgentSummary> result,
        AgentConfigEntity entity,
        string? tenantId)
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

        result.RemoveAll(item =>
            string.Equals(item.TenantId, ownerTenantId, StringComparison.Ordinal)
            && string.Equals(item.AgentId, entity.AgentId, StringComparison.OrdinalIgnoreCase));
        result.Add(new AgentSummary
        {
            TenantId = ownerTenantId,
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
