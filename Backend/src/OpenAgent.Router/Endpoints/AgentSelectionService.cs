using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionService(
    IAgentVisibilityService visibilityService,
    IConversationAgentResolver conversationAgentResolver,
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
            AgentSelectionResult? conversationResult = await ResolveConversationAgentAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (conversationResult != null)
            {
                if (!conversationResult.IsSelected)
                {
                    return conversationResult;
                }

                selectedAgentId = conversationResult.AgentId;
            }
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
            return AgentSelectionResult.Failed(AgentSelectionFailure.NoAgentAvailable);
        }

        bool visible = await visibilityService.IsAgentVisibleToUserAsync(
            selectedAgentId,
            request.UserContext,
            cancellationToken).ConfigureAwait(false);
        if (!visible)
        {
            return AgentSelectionResult.Failed(AgentSelectionFailure.Forbidden);
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

    private async Task<AgentSelectionResult?> ResolveConversationAgentAsync(
        AgentSelectionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ConversationAgentResolution resolution = await conversationAgentResolver.ResolveAsync(
                request.TargetEndpoint,
                request.ConversationId!,
                request.Identity,
                cancellationToken).ConfigureAwait(false);
            return resolution.Exists
                ? AgentSelectionResult.Selected(resolution.AgentId!)
                : null;
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            return AgentSelectionResult.Failed(AgentSelectionFailure.Forbidden);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            RouterLog.ConversationAgentResolutionFailed(
                logger,
                exception,
                request.ConversationId!,
                request.TraceId);
            return AgentSelectionResult.Failed(AgentSelectionFailure.DependencyUnavailable);
        }
    }
}
