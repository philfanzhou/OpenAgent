using System.Text.Json;
using StackExchange.Redis;

namespace OpenAgent.Router;

/// <summary>
/// Writes registrations received over HTTP into the existing <c>engine:registry:*</c> keys,
/// so Engines that still register through Redis directly and HTTP-registered ones share one pool.
/// </summary>
public sealed class EngineRegistryWriter(IConnectionMultiplexer redis, TimeProvider timeProvider)
{
    public const int MinTtlSeconds = 5;
    public const int MaxTtlSeconds = 300;

    public async Task PutAsync(string instanceId, Uri baseUrl, RegistrationRequest request)
    {
        EngineRegistryEntry entry = new()
        {
            EngineId = instanceId,
            Host = baseUrl.Host,
            Port = baseUrl.Port,
            Load = request.Load,
            LastHeartbeat = timeProvider.GetUtcNow().UtcDateTime,
            Intents = request.Intents
        };
        TimeSpan ttl = TimeSpan.FromSeconds(Math.Clamp(request.TtlSeconds, MinTtlSeconds, MaxTtlSeconds));
        IDatabase database = redis.GetDatabase();
        await database.StringSetAsync(
            EngineRegistryKeys.ForEngine(instanceId), JsonSerializer.Serialize(entry), ttl).ConfigureAwait(false);
        await database.SetAddAsync(EngineRegistryKeys.Index, instanceId).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string instanceId)
    {
        IDatabase database = redis.GetDatabase();
        await database.KeyDeleteAsync(EngineRegistryKeys.ForEngine(instanceId)).ConfigureAwait(false);
        await database.SetRemoveAsync(EngineRegistryKeys.Index, instanceId).ConfigureAwait(false);
    }
}
