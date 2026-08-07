using OpenAgent.Contracts.Security;
using OpenAgent.Router.Security;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentVisibilityChecker(
    IIntentRecognizer intentRecognizer,
    IAgentVisibilityService visibilityService)
{
    internal async Task<(string Intent, bool IsVisible)> CheckAsync(
        string query,
        string? agentId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var intent = await intentRecognizer.RecognizeAsync(query, cancellationToken);
        var isVisible = string.IsNullOrEmpty(agentId)
            || await visibilityService.IsAgentVisibleToUserAsync(agentId, userContext, cancellationToken);
        return (intent, isVisible);
    }
}
