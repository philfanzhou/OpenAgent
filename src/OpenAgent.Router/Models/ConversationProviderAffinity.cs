namespace OpenAgent.Router.Models;

internal enum ConversationAffinityState
{
    Pending,
    Confirmed
}

internal sealed record ConversationProviderAffinity(
    string ProviderId,
    ConversationAffinityState State);
