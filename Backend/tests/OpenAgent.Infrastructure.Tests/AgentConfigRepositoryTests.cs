using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Infrastructure.Configuration;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public class AgentConfigRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_StaleVersion_PreservesCurrentConfiguration()
    {
        await using ServiceProvider services = CreateServices();
        IAgentConfigRepository repository = CreateRepository(services);
        AgentConfigEntity created = Assert.IsType<AgentConfigEntity>(
            await repository.UpsertAsync(
                "tenant-a",
                "support",
                CreateEntity("initial"),
                expectedVersion: null));
        AgentConfigEntity updated = Assert.IsType<AgentConfigEntity>(
            await repository.UpsertAsync(
                "tenant-a",
                "support",
                CreateEntity("updated"),
                created.CurrentVersion));

        AgentConfigEntity? stale = await repository.UpsertAsync(
            "tenant-a",
            "support",
            CreateEntity("stale"),
            created.CurrentVersion);
        AgentConfigEntity stored = Assert.IsType<AgentConfigEntity>(
            await repository.GetAsync("tenant-a", "support"));

        Assert.Equal("1", created.CurrentVersion);
        Assert.Equal("2", updated.CurrentVersion);
        Assert.Null(stale);
        Assert.Equal("updated", stored.Config.Instructions);
    }

    [Fact]
    public async Task UpsertAsync_SameAgentId_IsolatedByTenant()
    {
        await using ServiceProvider services = CreateServices();
        IAgentConfigRepository repository = CreateRepository(services);
        await repository.UpsertAsync(
            "tenant-a",
            "support",
            CreateEntity("tenant-a"),
            expectedVersion: null);
        AgentConfigEntity foreign = CreateEntity("tenant-b");
        foreign.TenantId = "tenant-b";
        foreign.Config.TenantId = "tenant-b";

        AgentConfigEntity? result = await repository.UpsertAsync(
            "tenant-b",
            "support",
            foreign,
            expectedVersion: null);
        AgentConfigEntity tenantA = Assert.IsType<AgentConfigEntity>(
            await repository.GetAsync("tenant-a", "support"));
        AgentConfigEntity tenantB = Assert.IsType<AgentConfigEntity>(
            await repository.GetAsync("tenant-b", "support"));

        Assert.NotNull(result);
        Assert.Equal("tenant-a", tenantA.Config.Instructions);
        Assert.Equal("tenant-b", tenantB.Config.Instructions);
    }

    [Fact]
    public async Task UpsertAsync_InlineRagApiKey_RejectsPersistence()
    {
        await using ServiceProvider services = CreateServices();
        IAgentConfigRepository repository = CreateRepository(services);
        AgentConfigEntity entity = CreateEntity("secret");
        entity.Config.Rag.Instances.Add(new RagInstanceConfig
        {
            Id = "knowledge",
            ApiKey = "must-not-be-stored"
        });

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.UpsertAsync(
                "tenant-a",
                "support",
                entity,
                expectedVersion: null));

        Assert.Contains("ApiKeySecretRef", exception.Message, StringComparison.Ordinal);
        Assert.Null(await repository.GetAsync("tenant-a", "support"));
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<OpenAgentDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        return services.BuildServiceProvider();
    }

    private static IAgentConfigRepository CreateRepository(ServiceProvider services) =>
        new EfCoreAgentConfigRepository(
            services.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>());

    private static AgentConfigEntity CreateEntity(string instructions) => new()
    {
        AgentId = "support",
        TenantId = "tenant-a",
        Config = new AgentConfig
        {
            TenantId = "tenant-a",
            Instructions = instructions
        }
    };
}
