using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Config;
using Xunit;

namespace OpenAgent.Engine.Tests.Skills;

public class SkillCatalogStoreTests
{
    [Fact]
    public async Task ReadAndWrite_DelegateToRepository()
    {
        var skill = CreateSkill("tenant-a", "database");
        var repository = new RecordingRepository(skill);
        SkillCatalogStore catalog = new(repository);

        Assert.Same(skill, await catalog.GetAsync("tenant-a", "lookup"));
        Assert.Contains(skill, await catalog.ListAsync("tenant-a"));
        await catalog.PublishAsync(CreateSkill("tenant-a", "updated"));
        await catalog.RemoveAsync("tenant-a", "lookup");

        Assert.Equal(["get", "list", "upsert", "delete"], repository.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ReadAndWrite_RejectMissingTenant(string tenantId)
    {
        SkillCatalogStore catalog = new(new RecordingRepository());

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListAsync(tenantId));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetAsync(tenantId, "skill"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.RemoveAsync(tenantId, "skill"));
    }

    [Fact]
    public async Task Publish_RejectMissingSkillId()
    {
        SkillCatalogStore catalog = new(new RecordingRepository());

        await Assert.ThrowsAsync<ArgumentException>(
            () => catalog.PublishAsync(new SkillInstanceConfig { TenantId = "tenant-a", Id = "" }));
    }

    internal static SkillInstanceConfig CreateSkill(string tenantId, string description) => new()
    {
        TenantId = tenantId,
        Id = "lookup",
        Name = "lookup",
        Type = SkillTypes.AgentSkill,
        Description = description
    };

    private sealed class RecordingRepository(params SkillInstanceConfig[] skills) : ISkillDefinitionRepository
    {
        private readonly List<SkillInstanceConfig> _skills = [.. skills];
        internal List<string> Calls { get; } = [];

        public Task<SkillInstanceConfig?> GetAsync(string tenantId, string skillId, CancellationToken cancellationToken = default)
        {
            Calls.Add("get");
            return Task.FromResult(_skills.FirstOrDefault(skill => skill.Id == skillId));
        }

        public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Calls.Add("list");
            return Task.FromResult<IReadOnlyList<SkillInstanceConfig>>(_skills);
        }

        public Task UpsertAsync(SkillInstanceConfig skill, CancellationToken cancellationToken = default)
        {
            Calls.Add("upsert");
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string tenantId, string skillId, CancellationToken cancellationToken = default)
        {
            Calls.Add("delete");
            return Task.FromResult(true);
        }
    }
}
