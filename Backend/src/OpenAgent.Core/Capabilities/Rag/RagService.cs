using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Capabilities.Rag;

internal sealed class RagService(
    IHttpClientFactory httpClientFactory,
    IRagRegistry ragRegistry,
    IEnumerable<IRagAdapter> adapters) : IRagService
{
    public async Task IndexDocumentAsync(
        string content,
        Dictionary<string, object>? metadata,
        string? ragInstanceId,
        RagConfig ragConfig,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        List<RagInstanceConfig> configs = GetAllowedRagConfigs(userContext, ragConfig);

        if (!configs.Any())
        {
            return;
        }

        var targetConfigs = configs;
        if (!string.IsNullOrEmpty(ragInstanceId))
        {
            targetConfigs = configs.Where(c => c.Id == ragInstanceId).ToList();
            if (!targetConfigs.Any())
            {
                return;
            }
        }

        var enrichedMetadata = EnrichMetadata(metadata, userContext);

        foreach (var config in targetConfigs)
        {
            await IndexToExternalAsync(content, enrichedMetadata, config, cancellationToken);
        }
    }

    public async Task<List<string>> SearchAsync(
        string query,
        int limit,
        RagConfig ragConfig,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        var results = await SearchDetailedAsync(
            query,
            limit,
            ragConfig,
            userContext,
            cancellationToken).ConfigureAwait(false);
        return results.Select(r => r.Content).ToList();
    }

    public async Task<List<SearchResult>> SearchDetailedAsync(
        string query,
        int limit,
        RagConfig ragConfig,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        List<RagInstanceConfig> configs = GetAllowedRagConfigs(userContext, ragConfig);

        if (!configs.Any())
        {
            return new List<SearchResult>();
        }

        var allResults = new List<SearchResult>();

        foreach (RagInstanceConfig config in configs)
        {
            List<SearchResult> instanceResults = await SearchExternalDetailedAsync(
                query,
                limit,
                config,
                userContext,
                cancellationToken).ConfigureAwait(false);
            allResults.AddRange(instanceResults);
        }

        return allResults
            .OrderByDescending(r => r.RelevanceScore)
            .Take(limit)
            .ToList();
    }

    private List<RagInstanceConfig> GetAllowedRagConfigs(
        IAgentUserContext userContext,
        RagConfig ragConfig)
    {
        var instances = new List<RagInstanceConfig>();

        if (ragConfig.Instances != null && ragConfig.Instances.Any())
        {
            instances.AddRange(ragConfig.Instances);
        }
        else if (ragConfig.EnabledRagInstanceIds != null && ragConfig.EnabledRagInstanceIds.Any())
        {
            var allInstances = ragRegistry.GetAllInstances();
            var enabledSet = new HashSet<string>(ragConfig.EnabledRagInstanceIds, StringComparer.OrdinalIgnoreCase);
            instances = allInstances.Where(i => enabledSet.Contains(i.Id)).ToList();
        }

        return instances
            .Where(c => c.Enabled)
            .Where(c => IsAllowedForUser(c, userContext))
            .ToList();
    }

    private bool IsAllowedForUser(RagInstanceConfig config, IAgentUserContext? userContext)
    {
        if (config.AllowedUserIds.Count == 0 && config.AllowedGroups.Count == 0 && config.AllowedTenantIds.Count == 0 && config.AllowedRoles.Count == 0)
        {
            return true;
        }

        if (userContext == null)
        {
            return false;
        }

        if (config.AllowedUserIds.Count > 0 && config.AllowedUserIds.Contains(userContext.UserId))
        {
            return true;
        }

        if (config.AllowedGroups.Count > 0 && userContext.Groups != null && config.AllowedGroups.Intersect(userContext.Groups).Any())
        {
            return true;
        }

        if (config.AllowedTenantIds.Count > 0 && userContext.TenantId != null && config.AllowedTenantIds.Contains(userContext.TenantId))
        {
            return true;
        }

        if (config.AllowedRoles.Count > 0 && userContext.Roles != null && config.AllowedRoles.Intersect(userContext.Roles).Any())
        {
            return true;
        }

        return false;
    }

    private async Task IndexToExternalAsync(string content, Dictionary<string, object> metadata, RagInstanceConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.ApiEndpoint))
        {
            return;
        }

        try
        {
            IRagAdapter? adapter = GetAdapter(config);
            if (adapter == null)
            {
                return;
            }

            HttpClient client = httpClientFactory.CreateClient();

            using HttpRequestMessage? request = adapter.BuildIndexRequest(config, content, metadata);
            if (request == null)
            {
                return;
            }

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) { }
    }

    private async Task<List<SearchResult>> SearchExternalDetailedAsync(
        string query,
        int limit,
        RagInstanceConfig config,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.ApiEndpoint))
        {
            return new List<SearchResult>();
        }

        try
        {
            IRagAdapter? adapter = GetAdapter(config);
            if (adapter == null)
            {
                return new List<SearchResult>();
            }

            HttpClient client = httpClientFactory.CreateClient();
            Dictionary<string, object> filters = BuildAclFilters(userContext);

            using HttpRequestMessage request = adapter.BuildSearchRequest(config, query, limit, filters);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return adapter.ParseSearchResponse(config, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new List<SearchResult>();
        }
    }

    private IRagAdapter? GetAdapter(RagInstanceConfig config)
    {
        return adapters.FirstOrDefault(adapter => adapter.CanHandle(config));
    }

    private static Dictionary<string, object> EnrichMetadata(Dictionary<string, object>? metadata, IAgentUserContext? userContext)
    {
        var enriched = metadata ?? new Dictionary<string, object>();

        enriched["indexed_at"] = DateTime.UtcNow.ToString("O");
        enriched["indexed_by"] = userContext?.UserId ?? "Agent.Engine";

        if (!enriched.ContainsKey("tenant_id"))
        {
            enriched["tenant_id"] = userContext?.TenantId ?? "default";
        }

        return enriched;
    }

    private static Dictionary<string, object> BuildAclFilters(IAgentUserContext? userContext)
    {
        var filters = new Dictionary<string, object>();

        if (userContext?.TenantId != null)
        {
            filters["tenant_id"] = userContext.TenantId;
        }
        else
        {
            filters["tenant_id"] = "default";
        }

        return filters;
    }
}
