using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace OpenAgent.Router;

internal sealed class IdempotencyStore(IConnectionMultiplexer? redis = null) : IIdempotencyStore
{
    private const string CompleteScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
            return 1
        end
        return 0
        """;
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);

    public async Task<IdempotencyAcquireResult> AcquireAsync(
        string key,
        string requestDigest,
        string ownerToken,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoreEntry pending = StoreEntry.Pending(requestDigest, ownerToken);
        string pendingValue = JsonSerializer.Serialize(pending, JsonOptions);
        if (redis != null)
        {
            return await AcquireRedisAsync(
                key,
                requestDigest,
                pendingValue,
                timeToLive,
                cancellationToken).ConfigureAwait(false);
        }

        return AcquireMemory(key, requestDigest, pendingValue, pending, timeToLive);
    }

    public async Task<bool> CompleteAsync(
        string key,
        string requestDigest,
        string ownerToken,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string pendingValue = JsonSerializer.Serialize(
            StoreEntry.Pending(requestDigest, ownerToken),
            JsonOptions);
        StoreEntry complete = StoreEntry.Completed(requestDigest, response);
        string completeValue = JsonSerializer.Serialize(complete, JsonOptions);
        if (redis != null)
        {
            RedisResult result = await redis.GetDatabase().ScriptEvaluateAsync(
                CompleteScript,
                [key],
                [pendingValue, completeValue, checked((long)timeToLive.TotalMilliseconds)])
                .ConfigureAwait(false);
            return (long)result == 1;
        }

        if (!_memory.TryGetValue(key, out MemoryEntry? existing)
            || existing.SerializedValue != pendingValue)
        {
            return false;
        }

        return _memory.TryUpdate(
            key,
            new MemoryEntry(
                complete,
                completeValue,
                DateTimeOffset.UtcNow.Add(timeToLive)),
            existing);
    }

    public async Task ReleaseAsync(
        string key,
        string requestDigest,
        string ownerToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string pendingValue = JsonSerializer.Serialize(
            StoreEntry.Pending(requestDigest, ownerToken),
            JsonOptions);
        if (redis != null)
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                ReleaseScript,
                [key],
                [pendingValue]).ConfigureAwait(false);
            return;
        }

        if (_memory.TryGetValue(key, out MemoryEntry? existing)
            && existing.SerializedValue == pendingValue)
        {
            _memory.TryRemove(new KeyValuePair<string, MemoryEntry>(key, existing));
        }
    }

    private static IdempotencyAcquireResult ToAcquireResult(
        string requestDigest,
        StoreEntry entry)
    {
        if (!string.Equals(entry.RequestDigest, requestDigest, StringComparison.Ordinal))
        {
            return new IdempotencyAcquireResult(IdempotencyAcquireStatus.RequestMismatch);
        }

        return entry.State switch
        {
            EntryState.Pending => new IdempotencyAcquireResult(
                IdempotencyAcquireStatus.InProgress),
            EntryState.Completed when entry.Response != null => new IdempotencyAcquireResult(
                IdempotencyAcquireStatus.Completed,
                entry.Response),
            _ => throw new InvalidDataException("The idempotency entry is invalid.")
        };
    }

    private async Task<IdempotencyAcquireResult> AcquireRedisAsync(
        string key,
        string requestDigest,
        string pendingValue,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        IDatabase database = redis!.GetDatabase();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool acquired = await database.StringSetAsync(
                key,
                pendingValue,
                timeToLive,
                When.NotExists).ConfigureAwait(false);
            if (acquired)
            {
                return new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RedisValue existingValue = await database.StringGetAsync(key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!existingValue.IsNullOrEmpty)
            {
                StoreEntry entry = JsonSerializer.Deserialize<StoreEntry>(
                    (string)existingValue!,
                    JsonOptions) ?? throw new InvalidDataException(
                        "The idempotency entry is empty.");
                return ToAcquireResult(requestDigest, entry);
            }
        }

        return new IdempotencyAcquireResult(IdempotencyAcquireStatus.InProgress);
    }

    private IdempotencyAcquireResult AcquireMemory(
        string key,
        string requestDigest,
        string pendingValue,
        StoreEntry pending,
        TimeSpan timeToLive)
    {
        while (true)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_memory.TryGetValue(key, out MemoryEntry? existing))
            {
                if (existing.ExpiresAt > now)
                {
                    return ToAcquireResult(requestDigest, existing.Entry);
                }

                _memory.TryRemove(new KeyValuePair<string, MemoryEntry>(key, existing));
                continue;
            }

            if (_memory.TryAdd(
                key,
                new MemoryEntry(pending, pendingValue, now.Add(timeToLive))))
            {
                return new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired);
            }
        }
    }

    private sealed record MemoryEntry(
        StoreEntry Entry,
        string SerializedValue,
        DateTimeOffset ExpiresAt);

    private sealed record StoreEntry(
        EntryState State,
        string RequestDigest,
        string? OwnerToken,
        CachedResponse? Response)
    {
        internal static StoreEntry Pending(string requestDigest, string ownerToken) =>
            new(EntryState.Pending, requestDigest, ownerToken, null);

        internal static StoreEntry Completed(string requestDigest, CachedResponse response) =>
            new(EntryState.Completed, requestDigest, null, response);
    }

    private enum EntryState
    {
        Pending,
        Completed
    }
}
