using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Conversation.Store;

internal sealed record ConversationSession(
    int CurrentVersion,
    int NextSequence,
    IReadOnlyList<ConversationMessage> History,
    string ContextGeneration = "");
