using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Execution.Resolvers;
using OpenAgent.Core.Exten;
using Xunit;

namespace OpenAgent.Core.Tests.Execution.Resolvers;

public class AgentIdResolverTests
{
    [Theory]
    [InlineData("agent-context", "agent-header", "agent-item", "agent-context")]
    [InlineData(null, "agent-header", "agent-item", "agent-header")]
    [InlineData(null, null, "agent-item", "agent-item")]
    [InlineData(null, null, null, "default")]
    [InlineData("  ", "agent-header", "agent-item", "agent-header")]
    [InlineData(null, "  ", "agent-item", "agent-item")]
    [InlineData(null, null, "  ", "default")]
    public void Resolve_UsesExplicitHeaderItemAndDefaultPriority(
        string? explicitAgentId,
        string? headerAgentId,
        string? itemAgentId,
        string expectedAgentId)
    {
        var httpContext = new DefaultHttpContext();
        if (headerAgentId != null)
        {
            httpContext.Request.Headers["X-Agent-Id"] = headerAgentId;
        }

        if (itemAgentId != null)
        {
            httpContext.Items["AgentId"] = itemAgentId;
        }

        var resolver = new AgentIdResolver(
            new HttpContextAccessor { HttpContext = httpContext });
        Dictionary<string, object>? context = explicitAgentId == null
            ? null
            : new Dictionary<string, object> { ["AgentId"] = explicitAgentId };

        var result = resolver.Resolve(context);

        Assert.Equal(expectedAgentId, result);
    }

    [Fact]
    public void Resolve_RequestContextHasAgentId_ReturnsRequestContextValue()
    {
        // Arrange
        var requestContext = CreatePopulatedRequestContext("agent-request-context");
        var resolver = new AgentIdResolver(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            requestContext);

        // Act
        var result = resolver.Resolve();

        // Assert
        Assert.Equal("agent-request-context", result);
    }

    [Fact]
    public void Resolve_ContextDictAndRequestContext_DictWins()
    {
        // Arrange
        var requestContext = CreatePopulatedRequestContext("agent-request-context");
        var resolver = new AgentIdResolver(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            requestContext);
        var context = new Dictionary<string, object> { ["AgentId"] = "agent-explicit" };

        // Act
        var result = resolver.Resolve(context);

        // Assert
        Assert.Equal("agent-explicit", result);
    }

    [Fact]
    public void Resolve_RequestContextNull_FallsBackToHeaderAndItems()
    {
        // Arrange: HttpContextAccessor shares a static AsyncLocal slot, so each
        // scenario must resolve before the next accessor assignment.
        var headerContext = new DefaultHttpContext();
        headerContext.Request.Headers["X-Agent-Id"] = "agent-header";
        var headerResolver = new AgentIdResolver(
            new HttpContextAccessor { HttpContext = headerContext });

        // Act
        var headerResult = headerResolver.Resolve();

        // Arrange
        var itemContext = new DefaultHttpContext();
        itemContext.Items["AgentId"] = "agent-item";
        var itemResolver = new AgentIdResolver(
            new HttpContextAccessor { HttpContext = itemContext });

        // Act
        var itemResult = itemResolver.Resolve();

        // Assert
        Assert.Equal("agent-header", headerResult);
        Assert.Equal("agent-item", itemResult);
    }

    private static AgentRequestContext CreatePopulatedRequestContext(string agentId)
    {
        var requestContext = new AgentRequestContext();
        IAgentUserContext userContext = new AgentUserContext { UserId = "user-1" };
        requestContext.Populate("user-1", null, agentId, null, "trace-1", userContext);
        return requestContext;
    }

    [Fact]
    public void AddAgentCore_RegistersAgentIdResolverAsScoped()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddSingleton<IAgentConfigProvider>(
            new FakeAgentConfigProvider(AgentRunTestFactory.CreateConfig()));
        services.AddAgentCore(configuration);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using IServiceScope firstScope = provider.CreateScope();
        using IServiceScope secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<AgentIdResolver>();
        var sameScope = firstScope.ServiceProvider.GetRequiredService<AgentIdResolver>();
        var second = secondScope.ServiceProvider.GetRequiredService<AgentIdResolver>();

        Assert.Same(first, sameScope);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void AddAgentCore_RegistersAgentRunAsScoped()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddSingleton<IAgentConfigProvider>(
            new FakeAgentConfigProvider(AgentRunTestFactory.CreateConfig()));
        services.AddAgentCore(configuration);

        var runType = typeof(OpenAgent.Core.Execution.AgentRun);

        Assert.NotNull(runType);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == runType
            && descriptor.ImplementationType == runType
            && descriptor.Lifetime == ServiceLifetime.Scoped);
    }
}
