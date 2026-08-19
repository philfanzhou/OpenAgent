using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Redis;

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

        _local[BuildLocalKey(skill.TenantId, skill.Id, skill.Type)] = skill;
        if (!redis.IsAvailable)
        {
            return;
        }

        string payload = JsonSerializer.Serialize(skill, JsonOptions);
        await redis.StringSetAsync(
            BuildItemKey(skill.TenantId, skill.Id, skill.Type),
            payload).ConfigureAwait(false);
        await redis.SetAddAsync(
            BuildIndexKey(skill.TenantId),
            SerializeIndexMember(skill.Id, skill.Type)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
        string tenantId,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<string, SkillInstanceConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (SkillInstanceConfig skill in _local.Values.Where(skill =>
            string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
            && (type == null || string.Equals(skill.Type, type, StringComparison.OrdinalIgnoreCase))))
        {
            result[BuildLocalKey(skill.TenantId, skill.Id, skill.Type)] = skill;
        }

        if (redis.IsAvailable)
        {
            RedisValue[] members = await redis.SetMembersAsync(BuildIndexKey(tenantId)).ConfigureAwait(false);
            foreach (RedisValue member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryDeserializeIndexMember(member.ToString(), out SkillCatalogKey? key)
                    || key == null
                    || (type != null && !string.Equals(key.Type, type, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SkillInstanceConfig? skill = await ReadRedisAsync(
                    tenantId,
                    key.SkillId,
                    key.Type).ConfigureAwait(false);
                if (skill != null)
                {
                    result[BuildLocalKey(tenantId, skill.Id, skill.Type)] = skill;
                }
            }
        }

        if (repository != null)
        {
            IReadOnlyList<SkillInstanceConfig> databaseSkills = await repository
                .ListAsync(tenantId, type, cancellationToken)
                .ConfigureAwait(false);
            foreach (SkillInstanceConfig skill in databaseSkills)
            {
                result[BuildLocalKey(tenantId, skill.Id, skill.Type)] = skill;
                _local[BuildLocalKey(tenantId, skill.Id, skill.Type)] = skill;
            }
        }

        return result.Values.OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public async Task<SkillInstanceConfig?> GetAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, skillId, type);
        cancellationToken.ThrowIfCancellationRequested();

        if (repository != null)
        {
            SkillInstanceConfig? databaseSkill = await repository
                .GetAsync(tenantId, skillId, type, cancellationToken)
                .ConfigureAwait(false);
            if (databaseSkill != null)
            {
                _local[BuildLocalKey(tenantId, skillId, type)] = databaseSkill;
                return databaseSkill;
            }
        }

        SkillInstanceConfig? redisSkill = redis.IsAvailable
            ? await ReadRedisAsync(tenantId, skillId, type).ConfigureAwait(false)
            : null;
        if (redisSkill != null)
        {
            _local[BuildLocalKey(tenantId, skillId, type)] = redisSkill;
            return redisSkill;
        }

        _local.TryGetValue(BuildLocalKey(tenantId, skillId, type), out SkillInstanceConfig? localSkill);
        return localSkill;
    }

    public async Task RemoveAsync(
        string tenantId,
        string skillId,
        string type,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantId, skillId, type);
        cancellationToken.ThrowIfCancellationRequested();

        if (repository != null)
        {
            await repository.DeleteAsync(tenantId, skillId, type, cancellationToken).ConfigureAwait(false);
        }

        _local.TryRemove(BuildLocalKey(tenantId, skillId, type), out _);
        if (!redis.IsAvailable)
        {
            return;
        }

        await redis.SetRemoveAsync(
            BuildIndexKey(tenantId),
            SerializeIndexMember(skillId, type)).ConfigureAwait(false);
        await redis.KeyDeleteAsync(BuildItemKey(tenantId, skillId, type)).ConfigureAwait(false);
    }

    private async Task<SkillInstanceConfig?> ReadRedisAsync(
        string tenantId,
        string skillId,
        string type)
    {
        RedisValue value = await redis.StringGetAsync(BuildItemKey(tenantId, skillId, type)).ConfigureAwait(false);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        SkillInstanceConfig? skill = JsonSerializer.Deserialize<SkillInstanceConfig>(value.ToString(), JsonOptions);
        return skill != null
            && string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
            && string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(skill.Type, type, StringComparison.OrdinalIgnoreCase)
                ? skill
                : null;
    }

    private static string BuildIndexKey(string tenantId) =>
        $"skill:published:index:{Hash(tenantId)}";

    private static string BuildItemKey(string tenantId, string skillId, string type) =>
        $"skill:registry:{Hash(tenantId)}:{Encode(type)}:{Encode(skillId)}";

    private static string BuildLocalKey(string tenantId, string skillId, string type) =>
        $"{tenantId}\n{type}\n{skillId}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string SerializeIndexMember(string skillId, string type) =>
        JsonSerializer.Serialize(new SkillCatalogKey(skillId, type), JsonOptions);

    private static bool TryDeserializeIndexMember(string value, out SkillCatalogKey? key)
    {
        try
        {
            key = JsonSerializer.Deserialize<SkillCatalogKey>(value, JsonOptions);
            return key != null;
        }
        catch (JsonException)
        {
            key = null;
            return false;
        }
    }

    private static void Validate(SkillInstanceConfig skill) =>
        ValidateKey(skill.TenantId, skill.Id, skill.Type);

    private static void ValidateKey(string tenantId, string skillId, string type)
    {
        ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(skillId) || string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Skill id and type are required.");
        }
    }

    private static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }
    }

    private sealed record SkillCatalogKey(string SkillId, string Type);
}
