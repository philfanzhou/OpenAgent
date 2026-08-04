using OpenAgent.Core.Abstract;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Core.Capabilities.Rag;

internal class RagService : IRagService
{
    private readonly ILogger<RagService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRagRegistry _ragRegistry;
    private readonly IEnumerable<IRagAdapter> _adapters;

    public RagService(
        ILogger<RagService> logger,
        IHttpClientFactory httpClientFactory,
        IRagRegistry ragRegistry,
        IEnumerable<IRagAdapter> adapters)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _ragRegistry = ragRegistry;
        _adapters = adapters;
    }

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
                RagLog.TargetInstanceNotFound(_logger, ragInstanceId);
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

        foreach (var config in configs)
        {
            try
            {
                var instanceResults = await SearchExternalDetailedAsync(
                    query,
                    limit,
                    config,
                    userContext,
                    cancellationToken).ConfigureAwait(false);
                allResults.AddRange(instanceResults);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RagLog.SearchInstanceFailed(_logger, ex, config.Id);
            }
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
            var allInstances = _ragRegistry.GetAllInstances();
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
            RagLog.InstanceMissingApiEndpointSkippingIndexing(_logger, config.Id);
            return;
        }

        try
        {
            var adapter = GetAdapter(config);
            if (adapter == null)
            {
                RagLog.NoAdapterFoundForIndexing(_logger, config.Id);
                return;
            }

            var client = _httpClientFactory.CreateClient();

            var request = adapter.BuildIndexRequest(config, content, metadata);
            if (request == null)
            {
                RagLog.AdapterDoesNotSupportIndexing(_logger, config.Id, adapter.AdapterName);
                return;
            }

            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagLog.IndexFailed(_logger, ex, config.Id);
        }
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
            RagLog.InstanceMissingApiEndpointSkippingSearch(_logger, config.Id);
            return new List<SearchResult>();
        }

        try
        {
            var adapter = GetAdapter(config);
            if (adapter == null)
            {
                RagLog.NoAdapterFoundForSearch(_logger, config.Id);
                return new List<SearchResult>();
            }

            var client = _httpClientFactory.CreateClient();
            var filters = BuildAclFilters(userContext);

            var request = adapter.BuildSearchRequest(config, query, limit, filters);
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            return adapter.ParseSearchResponse(config, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagLog.SearchFailed(_logger, ex, config.Id);
            return new List<SearchResult>();
        }
    }

    private IRagAdapter? GetAdapter(RagInstanceConfig config)
    {
        return _adapters.FirstOrDefault(a => a.CanHandle(config));
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
