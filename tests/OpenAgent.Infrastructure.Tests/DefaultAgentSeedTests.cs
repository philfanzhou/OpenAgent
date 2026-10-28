using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Infrastructure.Configuration;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public sealed class DefaultAgentSeedTests
{
    [Fact]
    public void AgentConfigurationModel_ContainsSeededDefaultAgents()
    {
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            // This test only inspects EF metadata; it never opens a database connection.
            .UseNpgsql("Host=unit-test;Database=model-only;Username=model;Password=model")
            .Options;
        using var context = new OpenAgentDbContext(options);

        IEntityType designTimeEntity = Assert.IsAssignableFrom<IEntityType>(
            context.GetService<IDesignTimeModel>().Model.FindEntityType(
                "OpenAgent.Infrastructure.Entities.AgentConfigurationEntity"));
        List<Dictionary<string, object?>> seed = designTimeEntity.GetSeedData()
            .Select(row => row.ToDictionary(pair => pair.Key, pair => pair.Value))
            .ToList();

        Assert.Equal(2, seed.Count);
        Assert.All(seed, row => Assert.Equal("development", row["TenantId"]));
        Assert.Contains(seed, row => (string)row["AgentId"]! == "default");
        Assert.Contains(seed, row => (string)row["AgentId"]! == "intent-router");
        Assert.All(seed, row => Assert.Equal(AgentPublishStatus.Published, row["Status"]));
        Assert.All(seed, row => Assert.Equal(1L, row["Version"]));
        Assert.All(seed, row =>
        {
            Assert.NotEmpty((string)row["Name"]!);
            Assert.NotEmpty((string)row["Description"]!);
            Assert.NotEmpty((string)row["Instructions"]!);
            Assert.Equal("{}", row["McpJson"]);
            Assert.Equal("{}", row["RagJson"]);
            Assert.Equal("{}", row["SkillsJson"]);
            Assert.Equal("{}", row["CodeExecutionJson"]);
        });
        Assert.Contains(
            "20260916022059_SeedDefaultAgents",
            context.Database.GetMigrations());
    }

    [Fact]
    public async Task AgentConfigRepository_EnsureCreated_ReturnsSeededDefaultAgents()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<OpenAgentDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IDbContextFactory<OpenAgentDbContext> factory = provider
            .GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        await using OpenAgentDbContext database = await factory.CreateDbContextAsync();
        await database.Database.EnsureCreatedAsync();
        var repository = new AgentConfigRepository(factory);

        List<string> agentIds = (await repository.ListAsync("development"))
            .Select(agent => agent.AgentId)
            .ToList();
        Assert.Equal(["default", "intent-router"], agentIds);

        AgentConfigEntity defaultAgent = Assert.IsType<AgentConfigEntity>(
            await repository.GetAsync("development", "default"));
        Assert.Equal(AgentPublishStatus.Published, defaultAgent.Status);
        Assert.NotEmpty(defaultAgent.Name);
        Assert.NotEmpty(defaultAgent.Description);
        Assert.NotEmpty(defaultAgent.Config.Instructions);
        Assert.Empty(defaultAgent.Config.Mcp.EnabledServerIds);
        Assert.Empty(defaultAgent.Config.Rag.Instances);
        Assert.Empty(defaultAgent.Config.Skills.Instances);
        Assert.False(defaultAgent.Config.CodeExecution.Enabled);
    }
}
