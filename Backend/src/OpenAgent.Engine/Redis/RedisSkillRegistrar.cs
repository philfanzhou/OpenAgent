using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

/// <summary>
/// Loads the Redis Skill catalog for discovery. This never creates an executor;
/// AgentConfig.Skills remains the only source of Agent-to-Skill bindings.
/// </summary>
internal sealed class RedisSkillRegistrar : RedisRegistrarBase<SkillInstanceConfig>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISkillCatalog _catalog;

    public RedisSkillRegistrar(
        IRedisConnectionProvider redis,
        ISkillCatalog catalog,
        ILogger<RedisSkillRegistrar> logger)
        : base(redis, logger)
    {
        _catalog = catalog;
    }

    protected override string RegistrarName => "Skill catalog";
    protected override string IndexKey => "skill:published:index";
    protected override string ItemKeyPrefix => "skill:registry";
    protected override SkillInstanceConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<SkillInstanceConfig>(json, JsonOptions);
    protected override string? GetItemId(SkillInstanceConfig item) => item.Id;
    protected override void Register(SkillInstanceConfig item) => _catalog.Register(item);
}
