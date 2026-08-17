using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionService(
    IAgentCatalogService catalog,
    IConversationProviderResolver conversations,
    IIntentAgentSelector intentAgentSelector,
    IAgentUserContext userContext,
    IOptions<IntentRecognitionOptions> options) : IAgentSelectionService
{
    private readonly IntentRecognitionOptions _options = options.Value;

    public async Task<AgentSelection?> SelectAsync(
        string message,
        string tenantId,
        string? conversationId,
        string? explicitAgentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new AgentRoutingException(
                StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidTenant,
                "Tenant ID is required");
        }

        AgentProviderRequestContext requestContext = new(tenantId, userContext);
        ConversationProviderAffinity? affinity = string.IsNullOrWhiteSpace(conversationId)
            ? null
            : await conversations.ResolveAsync(
                requestContext,
                conversationId,
                cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(explicitAgentId))
        {
            AgentCatalogEntry entry = await catalog.ResolveAsync(
                requestContext,
                explicitAgentId,
                cancellationToken).ConfigureAwait(false);
            if (affinity != null
                && !string.Equals(
                    affinity.ProviderId,
                    entry.ProviderId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentRoutingException(
                    StatusCodes.Status409Conflict,
                    RouterErrorCodes.ConversationProviderMismatch,
                    "Agent does not belong to the Conversation Provider");
            }

            await EnsureConversationBindingAsync(
                requestContext,
                conversationId,
                affinity,
                entry.ProviderId,
                cancellationToken).ConfigureAwait(false);
            return new AgentSelection(entry.Agent.AgentId, entry.ProviderId);
        }

        if (affinity != null)
        {
            return new AgentSelection(null, affinity.ProviderId);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        try
        {
            AgentCatalogSnapshot snapshot = await catalog.GetAuthorizedAsync(
                requestContext,
                timeout.Token).ConfigureAwait(false);
            AgentCatalogEntry? selected = await SelectNewAsync(
                message,
                snapshot,
                timeout.Token).ConfigureAwait(false);
            if (selected == null)
            {
                return null;
            }

            await EnsureConversationBindingAsync(
                requestContext,
                conversationId,
                affinity,
                selected.ProviderId,
                cancellationToken).ConfigureAwait(false);
            return new AgentSelection(selected.Agent.AgentId, selected.ProviderId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable,
                "Agent selection timed out");
        }
    }

    private async Task<AgentCatalogEntry?> SelectNewAsync(
        string message,
        AgentCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        AgentCatalogEntry[] candidates = snapshot.Entries
            .Where(entry => !string.Equals(
                entry.Agent.AgentId,
                _options.AgentId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (_options.Enabled)
        {
            string? selectedAgentId = await intentAgentSelector.SelectAsync(
                message,
                candidates.Select(entry => entry.Agent).ToArray(),
                cancellationToken).ConfigureAwait(false);
            AgentCatalogEntry? selected = candidates.FirstOrDefault(entry =>
                string.Equals(
                    entry.Agent.AgentId,
                    selectedAgentId,
                    StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                return selected;
            }
        }

        return string.IsNullOrWhiteSpace(_options.FallbackAgentId)
            ? null
            : candidates.FirstOrDefault(entry => string.Equals(
                entry.Agent.AgentId,
                _options.FallbackAgentId,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureConversationBindingAsync(
        AgentProviderRequestContext requestContext,
        string? conversationId,
        ConversationProviderAffinity? affinity,
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || affinity != null)
        {
            return;
        }

        ConversationProviderAffinity bound = await conversations.BindPendingAsync(
            requestContext,
            conversationId,
            providerId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(bound.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentRoutingException(
                StatusCodes.Status409Conflict,
                RouterErrorCodes.ConversationProviderMismatch,
                "Conversation was concurrently bound to another Provider");
        }
    }
}
