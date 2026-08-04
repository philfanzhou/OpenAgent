using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Execution.Resolvers;
using OpenAgent.Core.Exten;
using Xunit;

namespace OpenAgent.Core.Tests.Execution.Resolvers;

public class AgentRequestContextTests
{
    [Fact]
    public void Defaults_BeforePopulate_ReturnsAnonymousDefaults()
    {
        // Arrange
        var context = new AgentRequestContext();

        // Act & Assert
        Assert.Equal("anonymous", context.UserId);
        Assert.Null(context.TenantId);
        Assert.Null(context.AgentId);
        Assert.Null(context.ConversationId);
        Assert.Equal(string.Empty, context.TraceId);
        Assert.NotNull(context.UserContext);
        Assert.Equal("anonymous", context.UserContext.UserId);
    }

    [Fact]
    public void Populate_ValidValues_SetsAllProperties()
    {
        // Arrange
        var context = new AgentRequestContext();
        IAgentUserContext userContext = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1"
        };

        // Act
        context.Populate("user-1", "tenant-1", "agent-1", "conv-1", "trace-1", userContext);

        // Assert
        Assert.Equal("user-1", context.UserId);
        Assert.Equal("tenant-1", context.TenantId);
        Assert.Equal("agent-1", context.AgentId);
        Assert.Equal("conv-1", context.ConversationId);
        Assert.Equal("trace-1", context.TraceId);
        Assert.Same(userContext, context.UserContext);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Populate_NullUserId_DefaultsToAnonymous(string? userId)
    {
        // Arrange
        var context = new AgentRequestContext();
        IAgentUserContext userContext = new AgentUserContext { UserId = "user-1" };

        // Act
        context.Populate(userId, "tenant-1", "agent-1", "conv-1", "trace-1", userContext);

        // Assert
        Assert.Equal("anonymous", context.UserId);
    }

    [Fact]
    public void Populate_NullTenantId_KeepsNull()
    {
        // Arrange
        var context = new AgentRequestContext();
        IAgentUserContext userContext = new AgentUserContext { UserId = "user-1" };

        // Act
        context.Populate("user-1", null, "agent-1", "conv-1", "trace-1", userContext);

        // Assert
        Assert.Null(context.TenantId);
    }

    [Fact]
    public void Populate_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        var context = new AgentRequestContext();
        IAgentUserContext userContext = new AgentUserContext { UserId = "user-1" };
        context.Populate("user-1", "tenant-1", "agent-1", "conv-1", "trace-1", userContext);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            context.Populate("user-2", "tenant-2", "agent-2", "conv-2", "trace-2", userContext));
    }

    [Fact]
    public void Override_AfterPopulate_OverridesOnlyProvidedValues()
    {
        // Arrange
        var context = new AgentRequestContext();
        IAgentUserContext userContext = new AgentUserContext { UserId = "user-1" };
        context.Populate("user-1", "tenant-1", "agent-1", "conv-1", "trace-1", userContext);

        // Act
        context.Override("agent-2", null);

        // Assert
        Assert.Equal("agent-2", context.AgentId);
        Assert.Equal("conv-1", context.ConversationId);
    }

    [Fact]
    public void AddAgentCore_ResolvesSameInstanceForInterfaceAndImplementation()
    {
        // Arrange
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddSingleton<IAgentConfigProvider>(
            new FakeAgentConfigProvider(AgentRunTestFactory.CreateConfig()));
        services.AddAgentCore(configuration);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using IServiceScope scope = provider.CreateScope();

        // Act
        var implementation = scope.ServiceProvider.GetRequiredService<AgentRequestContext>();
        var viaInterface = scope.ServiceProvider.GetRequiredService<IAgentRequestContext>();
        var viaWriter = scope.ServiceProvider.GetRequiredService<IAgentRequestContextWriter>();

        // Assert
        Assert.Same(implementation, viaInterface);
        Assert.Same(implementation, viaWriter);
    }
}
