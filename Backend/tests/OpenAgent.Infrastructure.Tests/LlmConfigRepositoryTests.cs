using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Infrastructure.Configuration;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public class LlmConfigRepositoryTests
{
    [Fact]
    public void PersistenceModel_UsesTypedConfigurationColumnsAndMatchesSnapshot()
    {
        using OpenAgentDbContext context = new(new DbContextOptionsBuilder<OpenAgentDbContext>()
            .UseNpgsql("Host=unit-test;Database=model-only;Username=model;Password=model").Options);
        var llm = context.Model.FindEntityType("OpenAgent.Infrastructure.Entities.LlmConfigurationEntity")!;
        var agent = context.Model.FindEntityType("OpenAgent.Infrastructure.Entities.AgentConfigurationEntity")!;

        Assert.Null(llm.FindProperty("ConfigurationJson"));
        Assert.Equal("integer", llm.FindProperty("ContextTokens")!.GetColumnType());
        Assert.Equal("text", llm.FindProperty("ApiKey")!.GetColumnType());
        Assert.Null(agent.FindProperty("ConfigurationJson"));
        Assert.Equal("text", agent.FindProperty("Instructions")!.GetColumnType());
        Assert.Equal("jsonb", agent.FindProperty("SkillsJson")!.GetColumnType());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains("20260903090000_UseConfigurationColumns", context.Database.GetMigrations());
    }

    [Fact]
    public void PersistenceModel_RegistersLlmConfigurationMigration()
    {
        DbContextOptions<OpenAgentDbContext> options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            .UseNpgsql("Host=unit-test;Database=model-only;Username=model;Password=model")
            .Options;
        using OpenAgentDbContext context = new(options);

        Assert.Contains(
            "20260902160000_AddLlmConfigurations",
            context.Database.GetMigrations());
    }

    [Fact]
    public async Task UpsertAsync_PersistsPlaintextKeyAndModelConfiguration()
    {
        await using ServiceProvider services = CreateServices();
        ILlmConfigRepository repository = CreateRepository(services);

        await repository.UpsertAsync("tenant-a", "openai", new LlmProviderProfile
        {
            Id = "ignored",
            Name = "OpenAI",
            ModelId = "gpt-4o",
            ContextTokens = 128_000,
            Endpoint = "https://api.openai.com/v1",
            ApiKey = "sk-plaintext",
            Format = ApiFormat.OpenAIResponses,
            Temperature = 0.2,
            Modality = ModelModality.Multimodal
        });
        LlmProviderProfile stored = Assert.IsType<LlmProviderProfile>(
            await repository.GetAsync("tenant-a", "openai"));

        Assert.Equal("tenant-a", stored.TenantId);
        Assert.Equal("openai", stored.Id);
        Assert.Equal("sk-plaintext", stored.ApiKey);
        Assert.Equal(128_000, stored.ContextTokens);
        Assert.Equal(ApiFormat.OpenAIResponses, stored.Format);
        Assert.Equal(0.2, stored.Temperature);
        Assert.Equal(ModelModality.Multimodal, stored.Modality);
    }

    [Fact]
    public async Task SameProfileId_IsIsolatedByTenant()
    {
        await using ServiceProvider services = CreateServices();
        ILlmConfigRepository repository = CreateRepository(services);
        await repository.UpsertAsync("tenant-a", "primary", Profile("key-a"));
        await repository.UpsertAsync("tenant-b", "primary", Profile("key-b"));

        LlmProviderProfile tenantA = Assert.IsType<LlmProviderProfile>(
            await repository.GetAsync("tenant-a", "primary"));
        LlmProviderProfile tenantB = Assert.IsType<LlmProviderProfile>(
            await repository.GetAsync("tenant-b", "primary"));

        Assert.Equal("key-a", tenantA.ApiKey);
        Assert.Equal("key-b", tenantB.ApiKey);
    }

    private static LlmProviderProfile Profile(string key) => new()
    {
        Name = "Primary",
        ModelId = "model",
        ContextTokens = 32_000,
        Endpoint = "https://llm.example.test",
        ApiKey = key
    };

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<OpenAgentDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        return services.BuildServiceProvider();
    }

    private static ILlmConfigRepository CreateRepository(ServiceProvider services) =>
        new LlmConfigRepository(
            services.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>());
}
