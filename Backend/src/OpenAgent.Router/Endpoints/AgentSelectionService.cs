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
        string? conversationId,
        string? explicitAgentId,
        CancellationToken cancellationToken,
        string? authenticationToken = null)
    {
        if (string.IsNullOrWhiteSpace(userContext.TenantId))
        {
            throw new AgentRoutingException(
                StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidTenant,
                "Tenant ID is required");
        }

        AgentProviderRequestContext requestContext = new(
            userContext,
            authenticationToken);
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
            AgentSelection selection = new(entry.Agent.AgentId, entry.ProviderId);
            RouterMeter.RecordProviderSelection("explicit");
            return selection;
        }

        if (affinity != null)
        {
            AgentSelection selection = new(null, affinity.ProviderId);
            RouterMeter.RecordProviderSelection("conversation");
            return selection;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        try
        {
            IReadOnlyList<AgentCatalogEntry> entries = await catalog.GetAuthorizedAsync(
                requestContext,
                timeout.Token).ConfigureAwait(false);
            (AgentCatalogEntry? Entry, string Source) selection = await SelectNewAsync(
                requestContext,
                message,
                entries,
                timeout.Token).ConfigureAwait(false);
            if (selection.Entry == null)
            {
                RouterMeter.RecordProviderSelection("unavailable");
                return null;
            }

            await EnsureConversationBindingAsync(
                requestContext,
                conversationId,
                affinity,
                selection.Entry.ProviderId,
                cancellationToken).ConfigureAwait(false);
            AgentSelection result = new(
                selection.Entry.Agent.AgentId,
                selection.Entry.ProviderId);
            RouterMeter.RecordProviderSelection(selection.Source);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable,
                "Agent selection timed out");
        }
    }

    private async Task<(AgentCatalogEntry? Entry, string Source)> SelectNewAsync(
        AgentProviderRequestContext requestContext,
        string message,
        IReadOnlyList<AgentCatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        AgentCatalogEntry[] candidates = entries
            .Where(entry => !string.Equals(
                entry.Agent.AgentId,
                _options.AgentId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (_options.Enabled)
        {
            string? selectedAgentId = await intentAgentSelector.SelectAsync(
                requestContext,
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
                return (selected, "intent");
            }
        }

        AgentCatalogEntry? fallback = string.IsNullOrWhiteSpace(_options.FallbackAgentId)
            ? null
            : candidates.FirstOrDefault(entry => string.Equals(
                entry.Agent.AgentId,
                _options.FallbackAgentId,
                StringComparison.OrdinalIgnoreCase));
        return (fallback, fallback == null ? "unavailable" : "fallback");
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
