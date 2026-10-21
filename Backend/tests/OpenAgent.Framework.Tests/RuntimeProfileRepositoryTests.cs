using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Framework.Configuration;
using Xunit;

namespace OpenAgent.Framework.Tests;

public sealed class RuntimeProfileRepositoryTests
{
    [Fact]
    public void MigrationHistory_IsExplicitlyPublicRegardlessOfPostgresUserSchema()
    {
        using OpenAgentDbContext context = new OpenAgentDbContextFactory().CreateDbContext([]);
        RelationalOptionsExtension options = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());
        Assert.Equal("public", options.MigrationsHistoryTableSchema);
        Assert.Equal("__EFMigrationsHistory", options.MigrationsHistoryTableName);
    }

    [Fact]
    public async Task SavedRuntimeAndMcp_AreVisibleToNewRepositoryAndIsolatedByTenant()
    {
        string database = Guid.NewGuid().ToString("N");
        ServiceProvider Services()
        {
            ServiceCollection services = new();
            services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseInMemoryDatabase(database));
            return services.BuildServiceProvider();
        }
        await using ServiceProvider first = Services();
        IDbContextFactory<OpenAgentDbContext> contexts = first.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        await new RuntimeProfileRepository(contexts).SaveAsync("tenant-a", new() { Id = "local", Name = "Saved", Provider = "OpenCode" });
        await new McpConfigRepository(contexts).SaveAsync("tenant-a", new() { Name = "lookup", Url = "https://example.test/mcp" });
        await using ServiceProvider second = Services();
        IDbContextFactory<OpenAgentDbContext> reloaded = second.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        RuntimeProfileRepository runtimes = new(reloaded);
        McpConfigRepository servers = new(reloaded);
        Assert.Equal("Saved", (await runtimes.GetAsync("tenant-a", "local"))!.Name);
        Assert.Equal("https://example.test/mcp", (await servers.GetAsync("tenant-a", "lookup"))!.Url);
        Assert.Null(await runtimes.GetAsync("tenant-b", "local"));
        Assert.Null(await servers.GetAsync("tenant-b", "lookup"));
        Assert.Single(await runtimes.ListAsync("tenant-a"));
        Assert.Empty(await runtimes.ListAsync("tenant-b"));
    }

    [Fact]
    public async Task AgentRepository_RoundTripsExistingOpenCodeModelWithoutMovingBindings()
    {
        ServiceCollection services = new();
        services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        await using ServiceProvider provider = services.BuildServiceProvider();
        AgentConfigRepository agents = new(provider.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>());
        await agents.UpsertAsync("tenant", "agent", new()
        {
            Config = new()
            {
                OpenCode = new() { Model = new() { ModelId = "saved-model", ApiKey = "v1.protected", Endpoint = "https://example.test/v1" } },
                Mcp = new() { EnabledServerIds = ["existing-mcp"] },
                Skills = new() { EnabledSkills = ["existing-skill"] }
            }
        }, null);
        var saved = (await agents.GetAsync("tenant", "agent"))!;
        Assert.Equal("saved-model", saved.Config.OpenCode.Model!.ModelId);
        Assert.Equal("v1.protected", saved.Config.OpenCode.Model.ApiKey);
        Assert.Equal("existing-mcp", Assert.Single(saved.Config.Mcp.EnabledServerIds));
        Assert.Equal("existing-skill", Assert.Single(saved.Config.Skills.EnabledSkills));
    }
}
