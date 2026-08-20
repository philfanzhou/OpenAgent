using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentModelResolver(IAgentRuntimeResolver runtime)
{
    internal async Task<AgentModelResolution> ResolveAsync(
        string agentId,
        AgentRequest request,
        LlmModelSelection? persistedConversationModel,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (request.MessageModelOverride != null)
        {
            AgentRuntimeProfile messageProfile = await runtime.ResolveAsync(
                agentId,
                user,
                request.MessageModelOverride,
                cancellationToken).ConfigureAwait(false);
            return new AgentModelResolution(
                messageProfile,
                LlmModelSelectionSource.Message,
                ApplyConversationUpdate: false,
                ConversationModel: null);
        }

        if (request.UpdateConversationModelOverride)
        {
            AgentRuntimeProfile conversationProfile = await runtime.ResolveAsync(
                agentId,
                user,
                request.ConversationModelOverride,
                cancellationToken).ConfigureAwait(false);
            return new AgentModelResolution(
                conversationProfile,
                request.ConversationModelOverride == null
                    ? LlmModelSelectionSource.Agent
                    : LlmModelSelectionSource.Conversation,
                ApplyConversationUpdate: true,
                ConversationModel: request.ConversationModelOverride);
        }

        if (persistedConversationModel != null)
        {
            try
            {
                AgentRuntimeProfile conversationProfile = await runtime.ResolveAsync(
                    agentId,
                    user,
                    persistedConversationModel,
                    cancellationToken).ConfigureAwait(false);
                return new AgentModelResolution(
                    conversationProfile,
                    LlmModelSelectionSource.Conversation,
                    ApplyConversationUpdate: false,
                    ConversationModel: null);
            }
            catch (AgentException exception) when (IsRecoverableModelFailure(exception.ErrorCode))
            {
                AgentRuntimeProfile fallbackProfile = await runtime.ResolveAsync(
                    agentId,
                    user,
                    modelOverride: null,
                    cancellationToken).ConfigureAwait(false);
                return new AgentModelResolution(
                    fallbackProfile,
                    LlmModelSelectionSource.AgentFallback,
                    ApplyConversationUpdate: true,
                    ConversationModel: null);
            }
        }

        AgentRuntimeProfile profile = await runtime.ResolveAsync(
            agentId,
            user,
            modelOverride: null,
            cancellationToken).ConfigureAwait(false);
        return new AgentModelResolution(
            profile,
            LlmModelSelectionSource.Agent,
            ApplyConversationUpdate: false,
            ConversationModel: null);
    }

    private static bool IsRecoverableModelFailure(AgentErrorCode errorCode) => errorCode is
        AgentErrorCode.PermissionDenied
        or AgentErrorCode.LlmModelNotFound
        or AgentErrorCode.LlmProviderNotSupported
        or AgentErrorCode.DependencyUnavailable
        or AgentErrorCode.ConfigurationError
        or AgentErrorCode.TenantDataIsolationViolation;
}

internal sealed record AgentModelResolution(
    AgentRuntimeProfile Profile,
    LlmModelSelectionSource Source,
    bool ApplyConversationUpdate,
    LlmModelSelection? ConversationModel);
