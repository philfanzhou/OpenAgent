using System.Net;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class HttpSkillCapabilitySourceTests
{
    [Fact]
    public async Task DiscoverAsync_TenantSkill_InvokesHttpEndpoint()
    {
        var catalog = new SkillCatalog();
        catalog.Register(CreateSkill("tenant-a"));
        var handler = new RecordingHandler();
        var source = new HttpSkillCapabilitySource(catalog, new TestHttpClientFactory(handler));

        IReadOnlyList<CapabilityDefinition> definitions = await source.DiscoverAsync(
            "agent-a",
            new AgentConfig { Skills = new SkillsConfig { EnabledSkills = ["lookup"] } },
            CreateUser("tenant-a"),
            default);
        string result = await Assert.Single(definitions).Invoke(
            new Dictionary<string, object?> { ["customerId"] = 42 },
            default);

        Assert.Equal("ok", result);
        Assert.Equal("https://example.test/lookup", handler.RequestUri?.ToString());
        Assert.Contains("\"customerId\":42", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_SkillFromAnotherTenant_ReturnsNoCapability()
    {
        var catalog = new SkillCatalog();
        catalog.Register(CreateSkill("tenant-a"));
        var source = new HttpSkillCapabilitySource(
            catalog,
            new TestHttpClientFactory(new RecordingHandler()));

        IReadOnlyList<CapabilityDefinition> definitions = await source.DiscoverAsync(
            "agent-b",
            new AgentConfig { Skills = new SkillsConfig { EnabledSkills = ["lookup"] } },
            CreateUser("tenant-b"),
            default);

        Assert.Empty(definitions);
    }

    private static SkillInstanceConfig CreateSkill(string tenantId) => new()
    {
        Id = "lookup",
        TenantId = tenantId,
        Name = "lookup",
        Description = "Looks up customers",
        ParametersJsonSchema = "{\"type\":\"object\"}",
        Type = SkillTypes.HttpEndpoint,
        SourceType = SkillSourceTypes.PostgreSql,
        EndpointUrl = "https://example.test/lookup"
    };

    private static AgentUserContext CreateUser(string tenantId) => new()
    {
        UserId = "user-a",
        TenantId = tenantId,
        IsAuthenticated = true
    };

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }
        internal string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        }
    }
}
