using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Redis;

internal sealed class RedisSkillCatalogStore(IRedisConnectionProvider redis) : ISkillCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task PublishAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable || string.IsNullOrWhiteSpace(skill.Id)) return;

        string payload = JsonSerializer.Serialize(skill, JsonOptions);
        await redis.StringSetAsync($"skill:registry:{skill.Id}", payload).ConfigureAwait(false);
        await redis.SetAddAsync("skill:published:index", skill.Id).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable) return [];

        RedisValue[] members = await redis.SetMembersAsync("skill:published:index").ConfigureAwait(false);
        var result = new List<SkillInstanceConfig>();
        foreach (RedisValue member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.IsNullOrEmpty) continue;
            RedisValue value = await redis.StringGetAsync($"skill:registry:{member}").ConfigureAwait(false);
            if (value.IsNullOrEmpty) continue;
            SkillInstanceConfig? skill = JsonSerializer.Deserialize<SkillInstanceConfig>(value.ToString(), JsonOptions);
            if (skill != null) result.Add(skill);
        }
        return result;
    }

    public async Task RemoveAsync(string skillId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable || string.IsNullOrWhiteSpace(skillId)) return;

        await redis.SetRemoveAsync("skill:published:index", skillId).ConfigureAwait(false);
        await redis.KeyDeleteAsync($"skill:registry:{skillId}").ConfigureAwait(false);
    }
}
