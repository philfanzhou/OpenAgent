namespace OpenAgent.Router.Models;

internal sealed record ConversationProviderAffinity(
    string ProviderId,
    ConversationAffinityState State);
