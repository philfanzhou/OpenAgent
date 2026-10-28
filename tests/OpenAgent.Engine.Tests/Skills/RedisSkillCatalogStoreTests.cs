using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Skills;

public class RedisSkillCatalogStoreTests
{
    [Fact]
    public async Task GetAsync_DatabaseAndRedisContainSameKey_ReturnsDatabaseDefinition()
    {
        var redis = new FakeRedisConnectionProvider();
        var redisOnly = new RedisSkillCatalogStore(redis);
        await redisOnly.PublishAsync(CreateSkill("tenant-a", "Redis"));
        var repository = new RecordingRepository(CreateSkill("tenant-a", "Database"));
        var catalog = new RedisSkillCatalogStore(redis, repository);

        SkillInstanceConfig? result = await catalog.GetAsync(
            "tenant-a",
            "lookup");

        Assert.Equal("Database", result?.Description);
    }

    [Fact]
    public async Task GetAsync_SameIdInAnotherTenant_DoesNotReturnSkill()
    {
        var redis = new FakeRedisConnectionProvider();
        var catalog = new RedisSkillCatalogStore(redis);
        await catalog.PublishAsync(CreateSkill("tenant-a", "Tenant A"));

        SkillInstanceConfig? result = await catalog.GetAsync(
            "tenant-b",
            "lookup");

        Assert.Null(result);
    }

    private static SkillInstanceConfig CreateSkill(string tenantId, string description) => new()
    {
        Id = "lookup",
        TenantId = tenantId,
        Name = "lookup",
        Description = description,
        Type = SkillTypes.AgentSkill,
        SourceType = SkillSourceTypes.ObjectStorage,
        ObjectKey = $"files/tenants/{tenantId}/skills/lookup/index.json"
    };

    private sealed class RecordingRepository(SkillInstanceConfig skill) : ISkillDefinitionRepository
    {
        public Task<SkillInstanceConfig?> GetAsync(
            string tenantId,
            string skillId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SkillInstanceConfig?>(
                string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase)
                    ? skill
                    : null);

        public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillInstanceConfig>>(
                string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
                    ? [skill]
                    : []);

        public Task UpsertAsync(
            SkillInstanceConfig definition,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(
            string tenantId,
            string skillId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
