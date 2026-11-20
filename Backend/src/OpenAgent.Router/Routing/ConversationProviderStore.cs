using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using OpenAgent.Router.Models;
using StackExchange.Redis;

namespace OpenAgent.Router.Routing;

internal sealed class ConversationProviderStore(
    IDistributedCache cache,
    IConnectionMultiplexer? redis = null) : IConversationProviderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // 亲和键必须有过期时间：否则随会话量无限增长（Redis 与本地内存都是）。新会话自然会重新绑定。
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(24);
    private const int LocalCapacity = 8192;

    private sealed record LocalEntry(ConversationProviderAffinity Affinity, DateTimeOffset CreatedAt);
    private readonly ConcurrentDictionary<string, LocalEntry> _local =
        new(StringComparer.Ordinal);

    private static bool IsExpired(LocalEntry entry) =>
        DateTimeOffset.UtcNow - entry.CreatedAt > KeyTtl;

    private void EvictOverflow()
    {
        if (_local.Count <= LocalCapacity)
        {
            return;
        }

        foreach ((string key, LocalEntry entry) in _local)
        {
            if (IsExpired(entry))
            {
                _local.TryRemove(key, out _);
            }
        }
    }

    public async Task<ConversationProviderAffinity?> GetAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        string key = BuildKey(tenantId, conversationId);
        if (redis != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisValue redisValue = await redis.GetDatabase()
                .StringGetAsync(key).ConfigureAwait(false);
            return redisValue.HasValue
                ? JsonSerializer.Deserialize<ConversationProviderAffinity>(
                    redisValue.ToString(),
                    JsonOptions)
                : null;
        }

        if (redis == null && _local.TryGetValue(key, out LocalEntry? local) && local is not null)
        {
            if (!IsExpired(local))
            {
                return local.Affinity;
            }

            _local.TryRemove(key, out _);
        }

        string? value = await cache.GetStringAsync(
            key,
            cancellationToken).ConfigureAwait(false);
        ConversationProviderAffinity? affinity = string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<ConversationProviderAffinity>(value, JsonOptions);
        if (affinity != null)
        {
            _local[key] = new LocalEntry(affinity, DateTimeOffset.UtcNow);
            EvictOverflow();
        }

        return affinity;
    }

    public async Task SetAsync(
        string tenantId,
        string conversationId,
        ConversationProviderAffinity affinity,
        CancellationToken cancellationToken)
    {
        string key = BuildKey(tenantId, conversationId);
        string value = JsonSerializer.Serialize(affinity, JsonOptions);
        if (redis != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await redis.GetDatabase().StringSetAsync(key, value, KeyTtl).ConfigureAwait(false);
            return;
        }

        _local[key] = new LocalEntry(affinity, DateTimeOffset.UtcNow);
        EvictOverflow();
        await cache.SetStringAsync(
            key,
            value,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = KeyTtl },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationProviderAffinity> BindAsync(
        string tenantId,
        string conversationId,
        ConversationProviderAffinity affinity,
        CancellationToken cancellationToken)
    {
        string key = BuildKey(tenantId, conversationId);
        if (redis == null)
        {
            if (_local.TryGetValue(key, out LocalEntry? existingLocal) && existingLocal is not null
                && !IsExpired(existingLocal))
            {
                return existingLocal.Affinity;
            }

            _local[key] = new LocalEntry(affinity, DateTimeOffset.UtcNow);
            EvictOverflow();
            await cache.SetStringAsync(
                key,
                JsonSerializer.Serialize(affinity, JsonOptions),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = KeyTtl },
                cancellationToken).ConfigureAwait(false);
            return affinity;
        }

        IDatabase database = redis.GetDatabase();
        cancellationToken.ThrowIfCancellationRequested();
        string value = JsonSerializer.Serialize(affinity, JsonOptions);
        if (await database.StringSetAsync(key, value, KeyTtl, When.NotExists)
            .ConfigureAwait(false))
        {
            return affinity;
        }

        RedisValue existing = await database.StringGetAsync(key).ConfigureAwait(false);
        return existing.HasValue
            ? JsonSerializer.Deserialize<ConversationProviderAffinity>(existing.ToString(), JsonOptions)
                ?? affinity
            : affinity;
    }

    private static string BuildKey(string tenantId, string conversationId)
    {
        byte[] input = Encoding.UTF8.GetBytes($"{tenantId}\0{conversationId}");
        return $"openagent:router:conversation-provider:{Convert.ToHexString(SHA256.HashData(input))}";
    }
}
