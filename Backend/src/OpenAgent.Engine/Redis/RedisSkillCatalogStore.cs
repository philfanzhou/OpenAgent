using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Redis;

/// <summary>
/// Persists Skill metadata in PostgreSQL and uses Redis only as a derived cache.
/// Redis is never used as the source of truth when the repository is available.
/// </summary>
internal sealed class RedisSkillCatalogStore(
    IRedisConnectionProvider redis,
    ISkillDefinitionRepository? repository = null) : ISkillCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, SkillInstanceConfig> _local =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task PublishAsync(
        SkillInstanceConfig skill,
        CancellationToken cancellationToken = default)
    {
        Validate(skill);
        cancellationToken.ThrowIfCancellationRequested();

        if (repository != null)
        {
            await repository.UpsertAsync(skill, cancellationToken).ConfigureAwait(false);
        }

        _local[BuildLocalKey(skill.TenantId, skill.Id)] = skill;
        if (!redis.IsAvailable)
        {
            return;
        }

        await redis.StringSetAsync(
            BuildItemKey(skill.TenantId, skill.Id),
            JsonSerializer.Serialize(skill, JsonOptions)).ConfigureAwait(false);
        await redis.SetAddAsync(
            BuildIndexKey(skill.TenantId),
            skill.Id).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        if (repository != null)
        {
            IReadOnlyList<SkillInstanceConfig> databaseSkills = await repository
                .ListAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            foreach (SkillInstanceConfig skill in databaseSkills)
            {
                _local[BuildLocalKey(tenantId, skill.Id)] = skill;
            }

            return databaseSkills;
        }

        var result = new Dictionary<string, SkillInstanceConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (SkillInstanceConfig skill in _local.Values.Where(skill =>
            string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
            && string.Equals(skill.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase)))
        {
            result[BuildLocalKey(tenantId, skill.Id)] = skill;
        }

        if (redis.IsAvailable)
        {
            RedisValue[] members = await redis.SetMembersAsync(BuildIndexKey(tenantId)).ConfigureAwait(false);
            foreach (RedisValue member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SkillInstanceConfig? skill = await ReadRedisAsync(
                    tenantId,
                    member.ToString()).ConfigureAwait(false);
                if (skill != null)
                {
                    result[BuildLocalKey(tenantId, skill.Id)] = skill;
                }
            }
        }

        return result.Values.OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public async Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, skillId);
        cancellationToken.ThrowIfCancellationRequested();

        if (repository != null)
        {
            SkillInstanceConfig? databaseSkill = await repository
                .GetAsync(tenantId, skillId, cancellationToken)
                .ConfigureAwait(false);
            if (databaseSkill != null)
            {
                _local[BuildLocalKey(tenantId, skillId)] = databaseSkill;
            }

            return databaseSkill;
        }

        SkillInstanceConfig? redisSkill = redis.IsAvailable
            ? await ReadRedisAsync(tenantId, skillId).ConfigureAwait(false)
            : null;
        if (redisSkill != null)
        {
            _local[BuildLocalKey(tenantId, skillId)] = redisSkill;
            return redisSkill;
        }

        _local.TryGetValue(BuildLocalKey(tenantId, skillId), out SkillInstanceConfig? localSkill);
        return localSkill;
    }

    public async Task RemoveAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, skillId);
        cancellationToken.ThrowIfCancellationRequested();

        if (repository != null)
        {
            await repository.DeleteAsync(tenantId, skillId, cancellationToken).ConfigureAwait(false);
        }

        _local.TryRemove(BuildLocalKey(tenantId, skillId), out _);
        if (!redis.IsAvailable)
        {
            return;
        }

        await redis.SetRemoveAsync(BuildIndexKey(tenantId), skillId).ConfigureAwait(false);
        await redis.KeyDeleteAsync(BuildItemKey(tenantId, skillId)).ConfigureAwait(false);
    }

    private async Task<SkillInstanceConfig?> ReadRedisAsync(string tenantId, string skillId)
    {
        RedisValue value = await redis.StringGetAsync(BuildItemKey(tenantId, skillId)).ConfigureAwait(false);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        SkillInstanceConfig? skill = JsonSerializer.Deserialize<SkillInstanceConfig>(value.ToString(), JsonOptions);
        return skill != null
            && string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
            && string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(skill.Type, SkillTypes.AgentSkill, StringComparison.OrdinalIgnoreCase)
                ? skill
                : null;
    }

    private static string BuildIndexKey(string tenantId) =>
        $"skill:published:index:{Hash(tenantId)}";

    private static string BuildItemKey(string tenantId, string skillId) =>
        $"skill:registry:{Hash(tenantId)}:{Encode(skillId)}";

    private static string BuildLocalKey(string tenantId, string skillId) =>
        $"{tenantId}\n{skillId}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void Validate(SkillInstanceConfig skill) =>
        ValidateKey(skill.TenantId, skill.Id);

    private static void ValidateKey(string tenantId, string skillId)
    {
        ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("Skill id is required.", nameof(skillId));
        }
    }

    private static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }
    }
}
