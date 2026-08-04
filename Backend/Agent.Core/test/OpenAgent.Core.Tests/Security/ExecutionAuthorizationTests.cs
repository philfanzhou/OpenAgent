using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public class ExecutionAuthorizationTests
{
    [Theory]
    [InlineData(AgentResourceType.Agent)]
    [InlineData(AgentResourceType.Model)]
    public async Task ExecuteAsync_DeniedAgentOrModel_DoesNotInvokeEngine(
        AgentResourceType deniedResourceType)
    {
        var engine = new RecordingEngine();
        var authorizationService = new SelectiveAuthorizationService(deniedResourceType);
        AgentRun run = AgentRunTestFactory.CreateRun(
            engine,
            new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance),
            AgentRunTestFactory.CreateConfig(),
            authorizationService: authorizationService);

        await Assert.ThrowsAsync<AgentException>(() => run.RunAsync(
            "hello",
            new Dictionary<string, object>
            {
                ["UserId"] = "user-1",
                ["TenantId"] = "tenant-1"
            },
            CancellationToken.None));

        Assert.Null(engine.LastRequest);
        Assert.Contains(
            authorizationService.Requests,
            request => request.ResourceType == deniedResourceType);
    }

    private sealed class SelectiveAuthorizationService : IAgentAuthorizationService
    {
        private readonly AgentResourceType _deniedResourceType;

        internal SelectiveAuthorizationService(AgentResourceType deniedResourceType)
        {
            _deniedResourceType = deniedResourceType;
        }

        internal List<AgentAuthorizationRequest> Requests { get; } = [];

        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(request.ResourceType != _deniedResourceType);
        }
    }
}
