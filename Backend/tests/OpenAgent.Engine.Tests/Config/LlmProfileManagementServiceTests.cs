using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Config;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class LlmProfileManagementServiceTests
{
    [Fact]
    public async Task SaveAsync_CommitsDatabaseThenRefreshesTenantCacheWithTtl()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new InMemoryRepository();
        LlmProfileManagementService service = CreateService(redis, repository);

        await service.SaveAsync(Profile("secret-a"), "tenant-a");
        RedisValue cached = redis.StringGet(
            LlmProfileManagementService.BuildKey("tenant-a", "primary"));

        Assert.Equal("secret-a", (await repository.GetAsync("tenant-a", "primary"))?.ApiKey);
        Assert.Contains("secret-a", cached.ToString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(5), redis.LastStringExpiry);
    }

    [Fact]
    public async Task SaveAsync_BlankKey_PreservesExistingPlaintextKey()
    {
        var repository = new InMemoryRepository();
        LlmProfileManagementService service = CreateService(
            new FakeRedisConnectionProvider(), repository);
        await service.SaveAsync(Profile("secret-a"), "tenant-a");
        LlmProviderProfile edited = Profile(string.Empty);
        edited.Name = "Renamed";

        LlmProviderProfile saved = await service.SaveAsync(edited, "tenant-a");

        Assert.Equal("secret-a", saved.ApiKey);
        Assert.Equal("Renamed", saved.Name);
    }

    [Fact]
    public async Task GetAsync_CacheMiss_BackfillsFromDatabaseWithoutCrossTenantLeak()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new InMemoryRepository();
        await repository.UpsertAsync("tenant-a", "primary", Profile("secret-a"));
        LlmProfileManagementService service = CreateService(redis, repository);

        LlmProviderProfile? own = await service.GetAsync("tenant-a", "primary");
        LlmProviderProfile? foreign = await service.GetAsync("tenant-b", "primary");

        Assert.Equal("secret-a", own?.ApiKey);
        Assert.Null(foreign);
        Assert.False(redis.StringGet(
            LlmProfileManagementService.BuildKey("tenant-a", "primary")).IsNullOrEmpty);
    }

    private static LlmProviderProfile Profile(string key) => new()
    {
        Id = "primary",
        Name = "Primary",
        ModelId = "model-1",
        ContextWindowTokens = 32_000,
        Endpoint = "https://llm.example.test",
        ApiKey = key
    };

    private static LlmProfileManagementService CreateService(
        FakeRedisConnectionProvider redis,
        ILlmConfigRepository repository) => new(
            redis,
            repository,
            Options.Create(new AgentConfigSourceOptions
            {
                RedisCacheTtlSeconds = 300,
                RedisCacheReconciliationSeconds = 60
            }),
            NullLogger<LlmProfileManagementService>.Instance);

    private sealed class InMemoryRepository : ILlmConfigRepository
    {
        private readonly Dictionary<(string TenantId, string ProfileId), LlmProviderProfile> _profiles = [];

        public Task<LlmProviderProfile?> GetAsync(
            string tenantId, string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.GetValueOrDefault((tenantId, profileId)));

        public Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderProfile>>(_profiles.Values
                .Where(profile => profile.TenantId == tenantId).ToArray());

        public Task<LlmProviderProfile> UpsertAsync(
            string tenantId, string profileId, LlmProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            profile.TenantId = tenantId;
            profile.Id = profileId;
            _profiles[(tenantId, profileId)] = profile;
            return Task.FromResult(profile);
        }

        public Task<bool> DeleteAsync(
            string tenantId, string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.Remove((tenantId, profileId)));
    }
}
