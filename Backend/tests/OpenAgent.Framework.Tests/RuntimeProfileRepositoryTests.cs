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
    public async Task GetAsync_CachesWithinTtl_AndWritePathsInvalidate()
    {
        ServiceCollection services = new();
        services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        await using ServiceProvider provider = services.BuildServiceProvider();
        CountingDbContextFactory counting = new(provider.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>());
        RuntimeProfileRepository runtimes = new(counting);
        McpConfigRepository servers = new(counting);

        await runtimes.SaveAsync("tenant", new() { Id = "runtime", Name = "First", Provider = "OpenCode" });
        await servers.SaveAsync("tenant", new() { Name = "mcp", Url = "https://example.test/mcp" });
        int afterSaves = counting.Created;
        // TTL 窗口内重复读取命中进程内缓存，不再创建 DbContext；负结果同样缓存。
        Assert.Equal("First", (await runtimes.GetAsync("tenant", "runtime"))!.Name);
        Assert.Equal("https://example.test/mcp", (await servers.GetAsync("tenant", "mcp"))!.Url);
        Assert.Null(await runtimes.GetAsync("tenant", "missing"));
        Assert.Null(await servers.GetAsync("tenant", "missing"));
        Assert.Equal(afterSaves + 4, counting.Created);

        // 写入路径必须失效缓存，保证同进程内读己之写。
        await runtimes.SaveAsync("tenant", new() { Id = "runtime", Name = "Second", Provider = "OpenCode" });
        Assert.Equal("Second", (await runtimes.GetAsync("tenant", "runtime"))!.Name);
        await servers.SaveAsync("tenant", new() { Name = "mcp", Url = "https://updated.test/mcp" });
        Assert.Equal("https://updated.test/mcp", (await servers.GetAsync("tenant", "mcp"))!.Url);
        // 删除路径与保存共用失效入口（InMemory 提供程序不支持 ExecuteDelete，删除语义由仓储实现保证）。
    }

    private sealed class CountingDbContextFactory(IDbContextFactory<OpenAgentDbContext> inner) : IDbContextFactory<OpenAgentDbContext>
    {
        public int Created { get; private set; }

        public OpenAgentDbContext CreateDbContext()
        {
            Created++;
            return inner.CreateDbContext();
        }

        public async Task<OpenAgentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Created++;
            return await inner.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        }
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
