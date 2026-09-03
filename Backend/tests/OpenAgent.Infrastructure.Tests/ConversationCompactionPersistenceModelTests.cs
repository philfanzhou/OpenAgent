using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public sealed class ConversationCompactionPersistenceModelTests
{
    [Fact]
    public void ConversationModel_ContextSummaries_UsesRequiredJsonbColumn()
    {
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            // This test only inspects EF metadata; it never opens a database connection.
            .UseNpgsql("Host=unit-test;Database=model-only;Username=model;Password=model")
            .Options;
        using var context = new OpenAgentDbContext(options);

        IEntityType conversation = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType("OpenAgent.Infrastructure.Entities.ConversationEntity"));
        IProperty property = Assert.IsAssignableFrom<IProperty>(
            conversation.FindProperty("ContextSummariesJson"));

        Assert.False(property.IsNullable);
        Assert.Equal("jsonb", property.GetColumnType());
        Assert.Contains(
            "20260819090000_AddConversationContextSummaries",
            context.Database.GetMigrations());
    }

    [Fact]
    public void ThirdPartyApiKeyModel_ContainsSeededCredentials()
    {
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            .UseNpgsql("Host=unit-test;Database=model-only;Username=model;Password=model")
            .Options;
        using var context = new OpenAgentDbContext(options);

        IEntityType entity = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType("OpenAgent.Infrastructure.Entities.ThirdPartyApiKeyEntity"));
        Assert.False(entity.FindProperty("KeyHash")!.IsNullable);
        Assert.False(entity.FindProperty("IsEnabled")!.IsNullable);
        Assert.Contains(
            "20260902090016_AddThirdPartyApiKeys",
            context.Database.GetMigrations());
        IEntityType designTimeEntity = Assert.IsAssignableFrom<IEntityType>(
            context.GetService<IDesignTimeModel>().Model.FindEntityType(
                "OpenAgent.Infrastructure.Entities.ThirdPartyApiKeyEntity"));
        Assert.Equal(2, designTimeEntity.GetSeedData().Count());
    }
}
