using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Execution.Resolvers;
using Moq;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities.Rag;

public class RagServiceTests
{
    [Fact]
    public async Task SearchAsync_RequestContextPopulated_PrefersRequestContextUserContextOverItems()
    {
        // Arrange
        var config = AgentRunTestFactory.CreateConfig();
        config.Rag.Instances.Add(new RagInstanceConfig
        {
            Id = "inst-1",
            ApiEndpoint = "http://rag.example",
            AllowedTenantIds = new List<string> { "ctx-tenant" }
        });

        IAgentUserContext requestUserContext = new AgentUserContext
        {
            UserId = "ctx-user",
            TenantId = "ctx-tenant",
            IsAuthenticated = true
        };
        var requestContext = new AgentRequestContext();
        requestContext.Populate("ctx-user", "ctx-tenant", null, null, "trace-1", requestUserContext);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AgentUserContext"] = new AgentUserContext
        {
            UserId = "items-user",
            TenantId = "items-tenant",
            IsAuthenticated = true
        };

        var adapter = new Mock<IRagAdapter>();
        adapter.Setup(a => a.CanHandle(It.IsAny<RagInstanceConfig>())).Returns(true);
        adapter.Setup(a => a.BuildSearchRequest(
                It.IsAny<RagInstanceConfig>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<Dictionary<string, object>?>()))
            .Returns(new HttpRequestMessage(HttpMethod.Get, "http://rag.example/search"));
        adapter.Setup(a => a.ParseSearchResponse(It.IsAny<RagInstanceConfig>(), It.IsAny<HttpResponseMessage>()))
            .Returns(new List<SearchResult>());

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new FakeHttpMessageHandler()));

        var ragRegistry = new Mock<IRagRegistry>();

        var service = new RagService(
            NullLogger<RagService>.Instance,
            httpClientFactory.Object,
            new FakeAgentConfigProvider(config),
            ragRegistry.Object,
            new HttpContextAccessor { HttpContext = httpContext },
            new AgentIdResolver(),
            new[] { adapter.Object },
            requestContext);

        // Act
        List<string> results = await service.SearchAsync("query");

        // Assert: the instance only allows ctx-tenant, so reaching the adapter
        // proves the request context user context won over the Items entry.
        adapter.Verify(a => a.BuildSearchRequest(
            It.IsAny<RagInstanceConfig>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<Dictionary<string, object>?>()), Times.Once);
        Assert.Empty(results);
    }
}
