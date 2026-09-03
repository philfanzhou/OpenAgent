using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class CapabilityToolFactoryTests
{
    private class FakeCapabilitySource : ICapabilitySource
    {
        private readonly List<CapabilityDefinition> _definitions;

        public FakeCapabilitySource(IEnumerable<CapabilityDefinition> definitions)
        {
            _definitions = definitions.ToList();
        }

        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CapabilityDefinition>>(_definitions);
    }

    private static CapabilityDefinition Tool(string name) => new(
        Name: name,
        Description: $"{name} tool",
        ParametersJsonSchema: "{\"type\":\"object\"}",
        ResourceType: AgentResourceType.Tool,
        ResourceId: name,
        Invoke: (_, _) => Task.FromResult("ok"));

    private static AgentUserContext Context() => new() { UserId = "u1" };

    private static CapabilityToolFactory Factory(
        ICapabilitySource source,
        IAgentAuthorizationService? auth = null)
    {
        var gate = new AgentAuthorizationGate(
            auth ?? new AllowAllAgentAuthorizationService());
        return new CapabilityToolFactory(new[] { source }, gate);
    }

    [Fact]
    public async Task CreateAsync_ReturnsToolsForDiscoveredDefinitions()
    {
        var source = new FakeCapabilitySource(new[] { Tool("search"), Tool("calc") });
        var factory = Factory(source);

        var tools = await factory.CreateAsync("a1", new AgentConfig(), Context(), default);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "search");
        Assert.Contains(tools, t => t.Name == "calc");
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        var source = new FakeCapabilitySource(new[] { Tool("dup"), Tool("dup") });
        var factory = Factory(source);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync("a1", new AgentConfig(), Context(), default));
    }

    [Fact]
    public async Task CreateAsync_UnauthorizedDefinition_IsExcluded()
    {
        var denyService = new DenyAuthorizationService();
        var source = new FakeCapabilitySource(new[] { Tool("secret") });
        var factory = Factory(source, denyService);

        var tools = await factory.CreateAsync("a1", new AgentConfig(), Context(), default);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task CreateAsync_UsesSingleAvailabilityAction()
    {
        var authorization = new RecordingAuthorizationService();
        var source = new FakeCapabilitySource(new[] { Tool("search") });
        var factory = Factory(source, authorization);

        IReadOnlyList<AITool> tools = await factory.CreateAsync(
            "a1",
            new AgentConfig(),
            Context(),
            default);

        Assert.Single(tools);
        Assert.NotEmpty(authorization.Actions);
        Assert.All(authorization.Actions, action => Assert.Equal("use", action));

        int availabilityChecks = authorization.Actions.Count;
        AIFunction tool = Assert.IsAssignableFrom<AIFunction>(tools[0]);
        object? result = await tool.InvokeAsync(new AIFunctionArguments(), default);

        Assert.Equal("ok", result);
        Assert.Equal(availabilityChecks, authorization.Actions.Count);
    }

    [Fact]
    public async Task CreateAsync_EmptySource_ReturnsEmpty()
    {
        var factory = Factory(new FakeCapabilitySource(Enumerable.Empty<CapabilityDefinition>()));

        var tools = await factory.CreateAsync("a1", new AgentConfig(), Context(), default);

        Assert.Empty(tools);
    }

    private class DenyAuthorizationService : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class RecordingAuthorizationService : IAgentAuthorizationService
    {
        internal List<string> Actions { get; } = [];

        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(request.Action);
            return Task.FromResult(true);
        }
    }
}
