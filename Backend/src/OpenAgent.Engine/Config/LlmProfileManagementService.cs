using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

internal sealed class LlmProfileManagementService(
    IRedisConnectionProvider redis,
    ILlmRegistry registry)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<IReadOnlyList<LlmProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            return registry.GetAllProfiles();
        }

        try
        {
            RedisValue[] ids = await redis.SetMembersAsync("llm:published:index").ConfigureAwait(false);
            var profiles = new List<LlmProviderProfile>();
            foreach (RedisValue id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LlmProviderProfile? profile = await GetFromRedisAsync(id.ToString()).ConfigureAwait(false);
                if (profile != null) profiles.Add(profile);
            }

            return profiles.Count > 0 ? profiles : registry.GetAllProfiles();
        }
        catch (RedisException)
        {
            return registry.GetAllProfiles();
        }
    }

    internal async Task<LlmProviderProfile?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            return registry.GetProfile(id);
        }

        try
        {
            return await GetFromRedisAsync(id).ConfigureAwait(false) ?? registry.GetProfile(id);
        }
        catch (RedisException)
        {
            return registry.GetProfile(id);
        }
    }

    internal async Task<LlmProviderProfile> SaveAsync(
        LlmProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.Register(profile);

        if (!redis.IsAvailable)
        {
            return profile;
        }

        string payload = JsonSerializer.Serialize(profile, JsonOptions);
        await redis.StringSetAsync($"llm:registry:{profile.Id}", payload).ConfigureAwait(false);
        await redis.SetAddAsync("llm:published:index", profile.Id).ConfigureAwait(false);
        await redis.GetDatabase().PublishAsync(
            RedisChannel.Literal("llm:registry:changed"),
            profile.Id).ConfigureAwait(false);
        return profile;
    }

    internal async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool removed = registry.Remove(id);
        if (!redis.IsAvailable)
        {
            return removed;
        }

        bool deleted = await redis.KeyDeleteAsync($"llm:registry:{id}").ConfigureAwait(false);
        await redis.SetRemoveAsync("llm:published:index", id).ConfigureAwait(false);
        await redis.GetDatabase().PublishAsync(
            RedisChannel.Literal("llm:registry:changed"),
            id).ConfigureAwait(false);
        return deleted || removed;
    }

    private async Task<LlmProviderProfile?> GetFromRedisAsync(string id)
    {
        RedisValue value = await redis.StringGetAsync($"llm:registry:{id}").ConfigureAwait(false);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<LlmProviderProfile>(value.ToString(), JsonOptions);
    }
}
