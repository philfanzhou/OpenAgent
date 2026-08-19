using OpenAgent.Router.Models;

namespace OpenAgent.Router.Routing;

internal sealed class ConversationProviderResolver(
    IAgentProviderRegistry providers,
    IConversationProviderStore store) : IConversationProviderResolver
{
    public async Task<ConversationProviderAffinity?> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ConversationProviderAffinity? affinity = await store.GetAsync(
            requestContext.TenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (affinity != null)
        {
            return await ResolveKnownAsync(
                requestContext,
                conversationId,
                affinity,
                cancellationToken).ConfigureAwait(false);
        }

        return await DiscoverAsync(
            requestContext,
            conversationId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ConversationProviderAffinity> BindPendingAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        string providerId,
        CancellationToken cancellationToken) => store.BindAsync(
            requestContext.TenantId,
            conversationId,
            new ConversationProviderAffinity(providerId, ConversationAffinityState.Pending),
            cancellationToken);

    private async Task<ConversationProviderAffinity> ResolveKnownAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        ConversationProviderAffinity affinity,
        CancellationToken cancellationToken)
    {
        if (!providers.TryGet(affinity.ProviderId, out IAgentProvider? provider)
            || provider == null)
        {
            throw ProviderUnavailable();
        }

        AgentProviderConversationStatus resolution = await ResolveProviderAsync(
            provider,
            requestContext,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (resolution == AgentProviderConversationStatus.Found)
        {
            if (affinity.State != ConversationAffinityState.Confirmed)
            {
                affinity = affinity with { State = ConversationAffinityState.Confirmed };
                await store.SetAsync(
                    requestContext.TenantId,
                    conversationId,
                    affinity,
                    cancellationToken).ConfigureAwait(false);
            }

            return affinity;
        }

        if (resolution == AgentProviderConversationStatus.Unavailable)
        {
            throw ProviderUnavailable();
        }

        if (resolution == AgentProviderConversationStatus.Forbidden)
        {
            throw ConversationNotFound();
        }

        if (affinity.State == ConversationAffinityState.Pending)
        {
            return affinity;
        }

        ConversationProviderAffinity? migrated = await DiscoverAsync(
            requestContext,
            conversationId,
            cancellationToken,
            affinity.ProviderId).ConfigureAwait(false);
        return migrated ?? throw ConversationNotFound();
    }

    private async Task<ConversationProviderAffinity?> DiscoverAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken,
        string? excludedProviderId = null)
    {
        List<string> owners = [];
        bool unavailable = false;
        bool forbidden = false;
        foreach (IAgentProvider provider in providers.Providers)
        {
            if (string.Equals(provider.Id, excludedProviderId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AgentProviderConversationStatus resolution = await ResolveProviderAsync(
                provider,
                requestContext,
                conversationId,
                cancellationToken).ConfigureAwait(false);
            switch (resolution)
            {
                case AgentProviderConversationStatus.Found:
                    owners.Add(provider.Id);
                    break;
                case AgentProviderConversationStatus.Forbidden:
                    forbidden = true;
                    break;
                case AgentProviderConversationStatus.Unavailable:
                    unavailable = true;
                    break;
            }
        }

        if (owners.Count > 1)
        {
            throw new AgentRoutingException(
                StatusCodes.Status409Conflict,
                RouterErrorCodes.ConversationOwnerConflict,
                "Conversation ownership is ambiguous");
        }

        if (unavailable)
        {
            throw new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.ConversationOwnerUnresolved,
                "Conversation owner could not be resolved");
        }

        if (owners.Count == 1)
        {
            ConversationProviderAffinity affinity = new(
                owners[0],
                ConversationAffinityState.Confirmed);
            await store.SetAsync(
                requestContext.TenantId,
                conversationId,
                affinity,
                cancellationToken).ConfigureAwait(false);
            return affinity;
        }

        if (forbidden)
        {
            throw ConversationNotFound();
        }

        return null;
    }

    private static async Task<AgentProviderConversationStatus> ResolveProviderAsync(
        IAgentProvider provider,
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ResolveConversationAsync(
                requestContext,
                conversationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return AgentProviderConversationStatus.Unavailable;
        }
    }

    private static AgentRoutingException ProviderUnavailable() => new(
        StatusCodes.Status503ServiceUnavailable,
        RouterErrorCodes.AgentProviderUnavailable,
        "Conversation Provider is unavailable");

    private static AgentRoutingException ConversationNotFound() => new(
        StatusCodes.Status404NotFound,
        RouterErrorCodes.ConversationNotFound,
        "Conversation was not found");
}
