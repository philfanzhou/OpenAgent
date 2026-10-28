using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace OpenAgent.Router;

internal sealed class RouterQueryCache(IConnectionMultiplexer? redis = null) : IQueryCache
{
    private static readonly TimeSpan LegacyTimeToLive = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);

    public async Task<CachedResponse?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (redis != null)
        {
            RedisValue value = await redis.GetDatabase().StringGetAsync(key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<CachedResponse>((string)value!, JsonOptions);
        }

        if (!_memory.TryGetValue(key, out MemoryEntry? entry))
        {
            return null;
        }

        if (entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return entry.Response;
        }

        _memory.TryRemove(new KeyValuePair<string, MemoryEntry>(key, entry));
        return null;
    }

    public async Task SetAsync(
        string key,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (redis != null)
        {
            string value = JsonSerializer.Serialize(response, JsonOptions);
            await redis.GetDatabase().StringSetAsync(key, value, timeToLive).ConfigureAwait(false);
            return;
        }

        _memory[key] = new MemoryEntry(response, DateTimeOffset.UtcNow.Add(timeToLive));
    }

    public async Task<string?> GetCachedResponseAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        CachedResponse? response = await GetAsync(query, cancellationToken).ConfigureAwait(false);
        return response == null ? null : System.Text.Encoding.UTF8.GetString(response.Body);
    }

    public Task SetCachedResponseAsync(
        string query,
        string response,
        CancellationToken cancellationToken = default)
    {
        return SetAsync(
            query,
            new CachedResponse(
                StatusCodes.Status200OK,
                "application/json",
                System.Text.Encoding.UTF8.GetBytes(response)),
            LegacyTimeToLive,
            cancellationToken);
    }

    private sealed record MemoryEntry(CachedResponse Response, DateTimeOffset ExpiresAt);
}
