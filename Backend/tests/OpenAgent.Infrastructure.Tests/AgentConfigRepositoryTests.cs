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
                "support",
                CreateEntity("initial"),
                expectedVersion: null));
        AgentConfigEntity updated = Assert.IsType<AgentConfigEntity>(
            await repository.UpsertAsync(
                "support",
                CreateEntity("updated"),
                created.CurrentVersion));

        AgentConfigEntity? stale = await repository.UpsertAsync(
            "support",
            CreateEntity("stale"),
            created.CurrentVersion);
        AgentConfigEntity stored = Assert.IsType<AgentConfigEntity>(
            await repository.GetAsync("support"));

        Assert.Equal("1", created.CurrentVersion);
        Assert.Equal("2", updated.CurrentVersion);
        Assert.Null(stale);
        Assert.Equal("updated", stored.Config.Instructions);
    }

    [Fact]
    public async Task UpsertAsync_DifferentTenant_DoesNotOverwriteConfiguration()
    {
        await using ServiceProvider services = CreateServices();
        IAgentConfigRepository repository = CreateRepository(services);
        await repository.UpsertAsync(
            "support",
            CreateEntity("tenant-a"),
            expectedVersion: null);
        AgentConfigEntity foreign = CreateEntity("tenant-b");
        foreign.TenantId = "tenant-b";
        foreign.Config.TenantId = "tenant-b";

        AgentConfigEntity? result = await repository.UpsertAsync(
            "support",
            foreign,
            expectedVersion: null);
        AgentConfigEntity stored = Assert.IsType<AgentConfigEntity>(
            await repository.GetAsync("support"));

        Assert.Null(result);
        Assert.Equal("tenant-a", stored.TenantId);
        Assert.Equal("tenant-a", stored.Config.Instructions);
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
