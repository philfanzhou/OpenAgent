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
    private readonly ConcurrentDictionary<string, ConversationProviderAffinity> _local =
        new(StringComparer.Ordinal);

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

        if (redis == null && _local.TryGetValue(key, out ConversationProviderAffinity? local))
        {
            return local;
        }

        string? value = await cache.GetStringAsync(
            key,
            cancellationToken).ConfigureAwait(false);
        ConversationProviderAffinity? affinity = string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<ConversationProviderAffinity>(value, JsonOptions);
        if (affinity != null)
        {
            _local.TryAdd(key, affinity);
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
            await redis.GetDatabase().StringSetAsync(key, value).ConfigureAwait(false);
            return;
        }

        _local[key] = affinity;
        await cache.SetStringAsync(
            key,
            value,
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
            ConversationProviderAffinity bound = _local.GetOrAdd(key, affinity);
            if (ReferenceEquals(bound, affinity))
            {
                await cache.SetStringAsync(
                    key,
                    JsonSerializer.Serialize(bound, JsonOptions),
                    cancellationToken).ConfigureAwait(false);
            }

            return bound;
        }

        IDatabase database = redis.GetDatabase();
        cancellationToken.ThrowIfCancellationRequested();
        string value = JsonSerializer.Serialize(affinity, JsonOptions);
        if (await database.StringSetAsync(key, value, when: When.NotExists)
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
