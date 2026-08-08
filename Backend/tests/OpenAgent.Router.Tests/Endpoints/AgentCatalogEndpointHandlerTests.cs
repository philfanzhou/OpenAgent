using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentCatalogEndpointHandlerTests
{
    [Fact]
    public async Task HandleAsync_AnonymousRequest_ReturnsUnauthorized()
    {
        var context = CreateContext();

        IResult result = await AgentCatalogEndpointHandler.HandleAsync(
            context,
            AnonymousUser,
            new StubRouteTable("http://engine"),
            new StubCatalog([]),
            context.RequestAborted);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NoEngine_ReturnsServiceUnavailable()
    {
        var context = CreateContext();

        IResult result = await AgentCatalogEndpointHandler.HandleAsync(
            context,
            AuthenticatedUser,
            new StubRouteTable(null),
            new StubCatalog([]),
            context.RequestAborted);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_VisibleAgents_ReturnsSummariesAndTrustedIdentity()
    {
        var context = CreateContext();
        context.Request.Headers.Authorization = "Basic encoded";
        context.Request.Headers["X-Trace-Id"] = "trace-1";
        var catalog = new StubCatalog(
        [
            new RoutableAgent(
                new AgentSummary
                {
                    AgentId = "support",
                    Name = "Support",
                    Description = "Support requests"
                },
                AgentDestinationKind.Engine,
                "http://engine")
        ]);

        IResult result = await AgentCatalogEndpointHandler.HandleAsync(
            context,
            AuthenticatedUser,
            new StubRouteTable("http://engine"),
            catalog,
            context.RequestAborted);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        AgentSummary[]? summaries = await JsonSerializer.DeserializeAsync<AgentSummary[]>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        AgentSummary summary = Assert.Single(Assert.IsType<AgentSummary[]>(summaries));
        Assert.Equal("support", summary.AgentId);
        Assert.NotNull(catalog.Request);
        Assert.Equal("http://engine", catalog.Request.EngineEndpoint);
        Assert.Equal("tenant-1", catalog.Request.Identity.TenantId);
        Assert.Equal("Basic encoded", catalog.Request.Identity.Authorization);
        Assert.Equal("trace-1", catalog.Request.Identity.TraceId);
        Assert.False(catalog.Request.IntentCandidatesOnly);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static AgentUserContext AuthenticatedUser => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private static AgentUserContext AnonymousUser => new()
    {
        UserId = string.Empty,
        IsAuthenticated = false
    };

    private sealed class StubRouteTable(string? endpoint) : IRouteTable
    {
        public string? GetTargetEndpoint(string intent) => endpoint;
    }

    private sealed class StubCatalog(IReadOnlyList<RoutableAgent> agents) : IAgentCatalog
    {
        public AgentCatalogRequest? Request { get; private set; }

        public Task<IReadOnlyList<RoutableAgent>> ListAsync(
            AgentCatalogRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(agents);
        }
    }
}
