using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

/// <summary>
/// PostgreSQL-owned LLM profiles with a tenant-scoped Redis TTL cache.
/// Plaintext API keys remain server-side and are redacted by management endpoints.
/// </summary>
internal sealed class LlmProfileManagementService : ILlmConfigProvider
{
    private const string KeyPrefix = "llm:config-cache:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IRedisConnectionProvider _redis;
    private readonly ILlmConfigRepository _repository;
    private readonly ILogger _logger;
    private readonly TimeSpan _cacheTtl;

    public LlmProfileManagementService(
        IRedisConnectionProvider redis,
        ILlmConfigRepository repository,
        IOptions<AgentConfigSourceOptions> options,
        ILogger<LlmProfileManagementService> logger)
    {
        _redis = redis;
        _repository = repository;
        _logger = logger;
        if (options.Value.RedisCacheTtlSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                AgentConfigSourceOptions.SectionName,
                options.Value.RedisCacheTtlSeconds,
                "ConfigurationStore:RedisCacheTtlSeconds must be greater than zero.");
        }
        _cacheTtl = TimeSpan.FromSeconds(options.Value.RedisCacheTtlSeconds);
    }

    public Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(tenantId, cancellationToken);

    public async Task<LlmProviderProfile?> GetAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_redis.IsAvailable)
        {
            try
            {
                RedisValue cached = await _redis.StringGetAsync(
                    BuildKey(tenantId, profileId)).ConfigureAwait(false);
                if (!cached.IsNullOrEmpty)
                {
                    LlmProviderProfile? profile = JsonSerializer.Deserialize<LlmProviderProfile>(
                        cached.ToString(), JsonOptions);
                    if (Matches(profile, tenantId, profileId)) return profile;
                }
            }
            catch (Exception exception) when (exception is RedisException or JsonException)
            {
                EngineLog.LlmConfigCacheReadFailed(_logger, exception, profileId);
            }
        }

        LlmProviderProfile? persisted = await _repository
            .GetAsync(tenantId, profileId, cancellationToken).ConfigureAwait(false);
        if (persisted != null) await TryWriteCacheAsync(persisted, cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    internal async Task<LlmProviderProfile> SaveAsync(
        LlmProviderProfile profile,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        LlmProviderProfile? existing = await _repository
            .GetAsync(tenantId, profile.Id, cancellationToken).ConfigureAwait(false);
        profile.TenantId = tenantId;
        if (existing != null
            && (string.IsNullOrWhiteSpace(profile.ApiKey)
                || profile.ApiKey.StartsWith("***", StringComparison.Ordinal)))
        {
            profile.ApiKey = existing.ApiKey;
        }

        LlmProviderProfile saved = await _repository
            .UpsertAsync(tenantId, profile.Id, profile, cancellationToken).ConfigureAwait(false);
        await TryWriteCacheAsync(saved, CancellationToken.None).ConfigureAwait(false);
        return saved;
    }

    internal async Task<bool> DeleteAsync(
        string profileId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        bool deleted = await _repository
            .DeleteAsync(tenantId, profileId, cancellationToken).ConfigureAwait(false);
        if (_redis.IsAvailable)
        {
            try
            {
                await _redis.KeyDeleteAsync(BuildKey(tenantId, profileId)).ConfigureAwait(false);
            }
            catch (RedisException exception)
            {
                EngineLog.LlmConfigCacheEvictionFailed(_logger, exception, profileId);
            }
        }
        return deleted;
    }

    internal static string BuildKey(string tenantId, string profileId) =>
        $"{KeyPrefix}{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(profileId)}";

    private async Task TryWriteCacheAsync(
        LlmProviderProfile profile,
        CancellationToken cancellationToken)
    {
        if (!_redis.IsAvailable) return;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _redis.StringSetAsync(
                BuildKey(profile.TenantId, profile.Id),
                JsonSerializer.Serialize(profile, JsonOptions),
                _cacheTtl).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RedisException or InvalidOperationException)
        {
            EngineLog.LlmConfigCacheWriteFailed(_logger, exception, profile.Id);
        }
    }

    private static bool Matches(
        LlmProviderProfile? profile,
        string tenantId,
        string profileId) =>
        profile != null
        && string.Equals(profile.TenantId, tenantId, StringComparison.Ordinal)
        && string.Equals(profile.Id, profileId, StringComparison.Ordinal);
}
