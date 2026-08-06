using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router.Security;

internal class AgentVisibilityService : IAgentVisibilityService
{
    private readonly ILogger<AgentVisibilityService> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ConcurrentDictionary<string, AgentAclEntry> _cache = new();
    private readonly AgentConfigAccessor _configAccessor;

    public AgentVisibilityService(IConnectionMultiplexer? redis, IConfiguration configuration, ILogger<AgentVisibilityService> logger)
    {
        _logger = logger;
        _redis = redis;
        _configAccessor = new AgentConfigAccessor(redis, logger);

        var redisConnectionString = configuration.GetConnectionString("Redis");
        RouterLog.VisibilityServiceInitialized(_logger, redisConnectionString, _redis != null);
    }

    public async Task<bool> IsAgentVisibleToUserAsync(string agentId, IAgentUserContext userContext, CancellationToken cancellationToken = default)
    {
        var acl = await GetAclEntryAsync(agentId, cancellationToken);

        if (acl == null)
        {
            RouterLog.AclEntryNotFound(_logger, agentId);
            return true;
        }

        return IsAllowedForUser(acl, userContext);
    }

    public async Task<List<string>> GetPublishedAgentIdsAsync(CancellationToken cancellationToken = default)
    {
        return await _configAccessor.GetPublishedAgentIdsAsync(cancellationToken);
    }

    public async Task<string?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken = default)
    {
        return await _configAccessor.GetAgentConfigAsync(agentId, cancellationToken);
    }

    private async Task<AgentAclEntry?> GetAclEntryAsync(string agentId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(agentId, out var cached))
        {
            return cached;
        }

        string? aclJson = null;

        if (_redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"agent:acl:{agentId}");
                aclJson = value.HasValue ? value.ToString() : null;
            }
            catch (Exception ex)
            {
                RouterLog.ReadAclEntryFailed(_logger, ex, agentId);
            }
        }

        if (aclJson != null)
        {
            RouterLog.AclRawJson(_logger, agentId, aclJson);
            var entry = JsonSerializer.Deserialize<AgentAclEntry>(aclJson);
            if (entry != null)
            {
                RouterLog.AclDeserialized(_logger, agentId,
                    string.Join(",", entry.AllowedUserIds),
                    string.Join(",", entry.AllowedGroups),
                    string.Join(",", entry.AllowedTenantIds),
                    string.Join(",", entry.AllowedRoles));
                _cache[agentId] = entry;
                return entry;
            }
        }

        return null;
    }

    private bool IsAllowedForUser(AgentAclEntry acl, IAgentUserContext userContext)
    {
        if (acl.AllowedUserIds.Count == 0
            && acl.AllowedGroups.Count == 0
            && acl.AllowedTenantIds.Count == 0
            && acl.AllowedRoles.Count == 0)
        {
            RouterLog.AclNoRestrictions(_logger, userContext.UserId);
            return true;
        }

        if (acl.AllowedUserIds.Count > 0 && acl.AllowedUserIds.Contains(userContext.UserId))
        {
            RouterLog.AllowedViaUserIds(_logger, userContext.UserId);
            return true;
        }

        if (acl.AllowedGroups.Count > 0 && userContext.Groups != null
            && acl.AllowedGroups.Intersect(userContext.Groups).Any())
        {
            RouterLog.AllowedViaGroups(_logger, userContext.UserId);
            return true;
        }

        if (acl.AllowedTenantIds.Count > 0 && userContext.TenantId != null
            && acl.AllowedTenantIds.Contains(userContext.TenantId))
        {
            RouterLog.AllowedViaTenantIds(_logger, userContext.UserId);
            return true;
        }

        if (acl.AllowedRoles.Count > 0 && userContext.Roles != null
            && acl.AllowedRoles.Intersect(userContext.Roles).Any())
        {
            RouterLog.AllowedViaRoles(_logger, userContext.UserId);
            return true;
        }

        RouterLog.AccessDeniedByAcl(_logger, userContext.UserId);
        return false;
    }
}
