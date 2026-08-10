using Microsoft.Extensions.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionService(
    IAgentVisibilityService visibilityService,
    IIntentAgentSelector intentAgentSelector,
    IOptions<IntentRecognitionOptions> options,
    ILogger<AgentSelectionService> logger) : IAgentSelectionService
{
    private readonly IntentRecognitionOptions _options = options.Value;

    public async Task<AgentSelectionResult> SelectAsync(
        AgentSelectionRequest request,
        CancellationToken cancellationToken)
    {
        string? selectedAgentId = request.ExplicitAgentId;
        if (string.IsNullOrWhiteSpace(selectedAgentId)
            && !string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return AgentSelectionResult.ContinueConversation();
        }

        bool selectedByIntentAgent = false;
        double? confidence = null;
        if (string.IsNullOrWhiteSpace(selectedAgentId))
        {
            IntentAgentDecision? decision = _options.Enabled
                ? await intentAgentSelector.SelectAsync(
                    new IntentAgentSelectionRequest(
                        request.Query,
                        request.TargetEndpoint,
                        request.Identity,
                        request.UserContext),
                    cancellationToken).ConfigureAwait(false)
                : null;
            selectedAgentId = decision?.AgentId ?? _options.FallbackAgentId;
            selectedByIntentAgent = decision != null;
            confidence = decision?.Confidence;
        }

        if (string.IsNullOrWhiteSpace(selectedAgentId))
        {
            return AgentSelectionResult.Failed(AgentSelectionStatus.NoAgentAvailable);
        }

        bool visible = await visibilityService.IsAgentVisibleToUserAsync(
            selectedAgentId,
            request.UserContext,
            cancellationToken).ConfigureAwait(false);
        if (!visible)
        {
            return AgentSelectionResult.Failed(AgentSelectionStatus.Forbidden);
        }

        RouterLog.AgentSelectionCompleted(
            logger,
            selectedAgentId,
            selectedByIntentAgent,
            confidence,
            request.TraceId);
        return AgentSelectionResult.Selected(
            selectedAgentId,
            selectedByIntentAgent,
            confidence);
    }
}
