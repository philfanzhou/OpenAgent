using System.Collections.Concurrent;
using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router.Security;

internal sealed class AgentVisibilityService : IAgentVisibilityService
{
    private readonly ILogger<AgentVisibilityService> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, AclSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly AgentConfigAccessor _configAccessor;
    private readonly TimeProvider _clock;

    public AgentVisibilityService(
        ILogger<AgentVisibilityService> logger,
        IConnectionMultiplexer? redis = null,
        TimeProvider? clock = null)
    {
        _logger = logger;
        _redis = redis;
        _clock = clock ?? TimeProvider.System;
        _configAccessor = new AgentConfigAccessor(redis, logger);
    }

    public async Task<bool> IsAgentVisibleToUserAsync(string agentId, IAgentUserContext userContext, CancellationToken cancellationToken = default)
    {
        var acl = await GetAclEntryAsync(agentId, cancellationToken);

        if (acl == null)
        {
            return true;
        }

        bool allowed = IsAllowedForUser(acl, userContext);
        if (!allowed)
        {
            RouterMeter.RecordAclDenial();
        }

        return allowed;
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
        cancellationToken.ThrowIfCancellationRequested();
        if (_redis is null)
        {
            // Router can also run in the explicit no-Redis mode.
            return null;
        }

        long readStarted = _clock.GetTimestamp();
        if (_cache.TryGetValue(agentId, out AclSnapshot? cached)
            && _clock.GetElapsedTime(cached.ReadStarted, readStarted) < SnapshotLifetime)
        {
            return cached.Entry;
        }

        try
        {
            RedisValue value = await _redis.GetDatabase().StringGetAsync($"agent:acl:{agentId}")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            AgentAclEntry? entry = value.IsNull ? null : JsonSerializer.Deserialize<AgentAclEntry>(value.ToString());
            if (!value.IsNull && (entry is null || entry.AllowedUserIds is null || entry.AllowedGroups is null
                || entry.AllowedTenantIds is null || entry.AllowedRoles is null))
            {
                throw new JsonException("Invalid Agent ACL record.");
            }
            AclSnapshot refreshed = new(entry, readStarted);
            return _cache.AddOrUpdate(agentId, refreshed,
                (_, current) => current.ReadStarted > readStarted ? current : refreshed).Entry;
        }
        catch (Exception error) when (error is RedisException or JsonException)
        {
            RouterLog.ReadAclEntryFailed(_logger, error, agentId);
            if (_cache.TryGetValue(agentId, out cached) && _clock.GetElapsedTime(cached.ReadStarted) < SnapshotLifetime)
            {
                return cached.Entry;
            }
            throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentAuthorizationUnavailable, "Agent authorization is temporarily unavailable");
        }
    }

    private sealed record AclSnapshot(AgentAclEntry? Entry, long ReadStarted);

    private static bool IsAllowedForUser(
        AgentAclEntry acl,
        IAgentUserContext userContext) =>
        acl.AllowedUserIds.Count == 0
            && acl.AllowedGroups.Count == 0
            && acl.AllowedTenantIds.Count == 0
            && acl.AllowedRoles.Count == 0
        || acl.AllowedUserIds.Contains(userContext.UserId)
        || userContext.Groups != null
            && acl.AllowedGroups.Intersect(userContext.Groups).Any()
        || userContext.TenantId != null
            && acl.AllowedTenantIds.Contains(userContext.TenantId)
        || userContext.Roles != null
            && acl.AllowedRoles.Intersect(userContext.Roles).Any();
}
