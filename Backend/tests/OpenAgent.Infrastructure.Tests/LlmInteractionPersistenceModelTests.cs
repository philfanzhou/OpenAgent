using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace OpenAgent.Infrastructure.Tests;

public sealed class LlmInteractionPersistenceModelTests
{
    [Fact]
    public void LlmInteractionModel_MapsTableColumnsAndIndexes()
    {
        using OpenAgentDbContext context = CreateModelOnlyContext();

        IEntityType entity = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType("OpenAgent.Infrastructure.Entities.LlmInteractionEntity"));
        Assert.Equal("openagent.llm_interaction_logs", entity.GetSchema() + "." + entity.GetTableName());

        Assert.Equal("jsonb", entity.FindProperty("RequestJson")!.GetColumnType());
        Assert.Equal("jsonb", entity.FindProperty("ResponseJson")!.GetColumnType());
        Assert.Equal(1024, entity.FindProperty("ErrorMessage")!.GetMaxLength());
        Assert.False(entity.FindProperty("TraceId")!.IsNullable);
        Assert.True(entity.FindProperty("ConversationId")!.IsNullable);

        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                ["TenantId", "ConversationId", "StartedAt"]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Single().Name == "TraceId");
        Assert.Contains(
            "20260917012452_AddLlmInteractionTraceability",
            context.Database.GetMigrations());
    }

    [Fact]
    public void ConversationMessageModel_TraceIdIsNullableColumn()
    {
        using OpenAgentDbContext context = CreateModelOnlyContext();

        IEntityType entity = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType("OpenAgent.Infrastructure.Entities.ConversationMessageEntity"));
        IProperty property = Assert.IsAssignableFrom<IProperty>(entity.FindProperty("TraceId"));

        Assert.True(property.IsNullable);
        Assert.Equal(256, property.GetMaxLength());
    }

    private static OpenAgentDbContext CreateModelOnlyContext()
    {
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            // This test only inspects EF metadata; it never opens a database connection.
            .UseNpgsql("Host=unit-test;Database=model-only;Username=model;Password=model")
            .Options;
        return new OpenAgentDbContext(options);
    }
}
