using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Tests.TestDoubles;

/// <summary>
/// Configurable in-memory authorization service for unit tests.
/// </summary>
public sealed class FakeAuthorizationService : IAgentAuthorizationService
{
    private readonly Func<AgentAuthorizationRequest, IAgentUserContext, bool> _evaluator;

    public FakeAuthorizationService(Func<AgentAuthorizationRequest, IAgentUserContext, bool>? evaluator = null)
    {
        _evaluator = evaluator ?? ((_, _) => true);
    }

    public static FakeAuthorizationService AllowAll() => new((_, _) => true);

    public static FakeAuthorizationService DenyAll() => new((_, _) => false);

    public Task<bool> IsAuthorizedAsync(
        AgentAuthorizationRequest request,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_evaluator(request, userContext));
}
