using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Capabilities.Mcp;
using OpenAgent.Application.Tooling.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Capabilities.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Capabilities;

public sealed class McpFeatureFactoryTests
{
    private const string AgentId = "agent-1";
    private static readonly AgentUserContext User = new() { UserId = "user-1", TenantId = "tenant-1" };

    [Fact]
    public async Task CreateAsync_InlineServers_ResolveToDefinitions()
    {
        McpFeatureFactory factory = CreateFactory(new AllowAllAgentAuthorizationService());

        AgentFeature feature = await factory.CreateAsync(AgentId, new AgentConfig
        {
            Mcp = new McpConfig
            {
                Servers =
                [
                    new McpServerConfig { Name = "localmcp", Url = "http://127.0.0.1:9/mcp", Type = McpServerType.Http, TenantId = "tenant-1" },
                    new McpServerConfig { Name = "events", Url = "http://127.0.0.1:9/sse", Type = McpServerType.SSE, TenantId = "tenant-1" }
                ]
            }
        }, User, CancellationToken.None);

        Assert.Empty(feature.Tools);
        (string, string, string)[] mapped = feature.McpServers
            .Select(server => (server.Name, server.Url, server.Type))
            .ToArray();
        Assert.Equal(("localmcp", "http://127.0.0.1:9/mcp", "http"), mapped[0]);
        Assert.Equal(("events", "http://127.0.0.1:9/sse", "sse"), mapped[1]);
        Assert.Equal(2, mapped.Length);
    }

    [Fact]
    public async Task CreateAsync_EnabledServerIds_ResolveThroughRepositoryAndDeduplicate()
    {
        var repository = new StubRepository(new McpServerConfig
        {
            Name = "published", Url = "http://published.test/mcp", Type = McpServerType.Http, TenantId = "tenant-1"
        });
        McpFeatureFactory factory = CreateFactory(new AllowAllAgentAuthorizationService(), repository);

        AgentFeature feature = await factory.CreateAsync(AgentId, new AgentConfig
        {
            Mcp = new McpConfig
            {
                EnabledServerIds = ["published", "published"],
                Servers = [new McpServerConfig { Name = "inline-ignored", Url = "http://inline.test" }]
            }
        }, User, CancellationToken.None);

        // EnabledServerIds 优先于内联 Servers；重复引用只透传一次。
        var server = Assert.Single(feature.McpServers);
        Assert.Equal("published", server.Name);
        Assert.Equal("http://published.test/mcp", server.Url);
    }

    [Fact]
    public async Task CreateAsync_UnknownServerId_IsSkipped()
    {
        McpFeatureFactory factory = CreateFactory(
            new AllowAllAgentAuthorizationService(), new StubRepository());

        AgentFeature feature = await factory.CreateAsync(AgentId, new AgentConfig
        {
            Mcp = new McpConfig
            {
                EnabledServerIds = ["missing"],
                Servers = [new McpServerConfig { Name = "inline", Url = "http://inline.test", TenantId = "tenant-1" }]
            }
        }, User, CancellationToken.None);

        // 注册表里不存在的 id 不报错；EnabledServerIds 仍优先于内联配置。
        Assert.Empty(feature.McpServers);
    }

    [Fact]
    public async Task CreateAsync_DifferentTenantServer_IsNotExposed()
    {
        McpFeatureFactory factory = CreateFactory(new AllowAllAgentAuthorizationService());

        AgentFeature feature = await factory.CreateAsync(AgentId, new AgentConfig
        {
            Mcp = new McpConfig
            {
                Servers = [new McpServerConfig { Name = "foreign", Url = "http://foreign.test", TenantId = "tenant-2" }]
            }
        }, User, CancellationToken.None);

        Assert.Empty(feature.McpServers);
    }

    [Fact]
    public async Task CreateAsync_UnauthorizedServer_IsNotExposed()
    {
        McpFeatureFactory factory = CreateFactory(new DenyMcpAuthorizationService());

        AgentFeature feature = await factory.CreateAsync(AgentId, new AgentConfig
        {
            Mcp = new McpConfig
            {
                Servers = [new McpServerConfig { Name = "secret", Url = "http://secret.test", TenantId = "tenant-1" }]
            }
        }, User, CancellationToken.None);

        Assert.Empty(feature.McpServers);
    }

    private static McpFeatureFactory CreateFactory(
        IAgentAuthorizationService authorization,
        IMcpConfigRepository? repository = null) =>
        new(new AgentAuthorizationGate(authorization), repository);

    private sealed class StubRepository(params McpServerConfig[] servers) : IMcpConfigRepository
    {
        private readonly List<McpServerConfig> _servers = [.. servers];

        public Task<McpServerConfig?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_servers.FirstOrDefault(server =>
                string.Equals(server.Name, id, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<McpServerConfig>> ListAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpServerConfig>>(_servers);

        public Task SaveAsync(string tenantId, McpServerConfig server, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DenyMcpAuthorizationService : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext user,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.ResourceType != AgentResourceType.Mcp);

        public Task EnsureAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext user,
            CancellationToken cancellationToken = default) =>
            throw new AgentException(AgentErrorCode.PermissionDenied, "denied");

        public Task<bool> IsAvailableAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext user,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.ResourceType != AgentResourceType.Mcp);
    }
}
