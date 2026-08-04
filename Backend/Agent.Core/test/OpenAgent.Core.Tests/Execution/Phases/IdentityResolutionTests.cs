using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Execution.Phases;
using OpenAgent.Core.Execution.Resolvers;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Execution.Phases;

public class IdentityResolutionTests
{
    [Fact]
    public async Task ResolveAsync_AgentExecuteDenied_ThrowsPermissionDenied()
    {
        // Arrange
        var resolution = CreateResolution(new DenyAllAuthorizationService());

        // Act
        var exception = await Assert.ThrowsAsync<AgentException>(() => resolution.ResolveAsync(
            new Dictionary<string, object>(),
            NullLogger.Instance,
            CancellationToken.None));

        // Assert
        Assert.Equal(AgentErrorCode.PermissionDenied, exception.ErrorCode);
    }

    [Fact]
    public async Task ResolveAsync_ConfigMissing_ThrowsInvalidOperation()
    {
        // Arrange
        var resolution = CreateResolution(
            new AllowAllAgentAuthorizationService(),
            new MissingConfigProvider());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolution.ResolveAsync(
            new Dictionary<string, object>(),
            NullLogger.Instance,
            CancellationToken.None));
    }

    private static IdentityResolution CreateResolution(
        IAgentAuthorizationService authorizationService,
        IAgentConfigProvider? configProvider = null)
    {
        return new IdentityResolution(
            new AgentIdResolver(),
            new ExecutionConfigResolver(
                configProvider ?? new FakeAgentConfigProvider(AgentRunTestFactory.CreateConfig()),
                NullLogger<ExecutionConfigResolver>.Instance),
            new UserContextBuilder(),
            new AgentAuthorizationGate(authorizationService),
            new LlmRegistry());
    }

    private sealed class DenyAllAuthorizationService : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class MissingConfigProvider : IAgentConfigProvider
    {
        public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentConfig());

        public Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentConfig?>(null);

        public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>(Array.Empty<AgentSummary>());
    }
}
