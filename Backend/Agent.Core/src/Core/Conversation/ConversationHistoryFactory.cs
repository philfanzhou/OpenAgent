using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Conversation;

internal sealed class ConversationHistoryFactory
{
    private readonly IConversationLock _conversationLock;
    private readonly ConversationSessionStore _store;
    private readonly ConversationStoreOptions _options;

    public ConversationHistoryFactory(
        IConversationLock conversationLock,
        ConversationSessionStore store,
        IOptions<ConversationStoreOptions> options)
    {
        _conversationLock = conversationLock;
        _store = store;
        _options = options.Value;
    }

    internal PlatformChatHistory Create(
        string agentId,
        AgentRequest request,
        IAgentUserContext user)
    {
        ConversationContext context = new(
            request.ConversationId,
            user.TenantId,
            user.UserId,
            agentId,
            request.TraceId);
        return new PlatformChatHistory(
            context,
            agentId,
            request.Query,
            request.Attachments,
            _conversationLock,
            _store);
    }

    internal AIContextProvider? CreateCompaction(ContextPolicy? policy, IChatClient chatClient)
    {
        CompactionStrategy? strategy = policy?.Strategy.ToLowerInvariant() switch
        {
            "summarize" => CreateSummarization(policy, chatClient),
            "sliding_window" => CreateSlidingWindow(policy),
            "none" => CreateDefaultTruncation(),
            null => CreateDefaultTruncation(),
            _ => CreateDefaultTruncation()
        };
        return strategy == null ? null : new CompactionProvider(strategy);
    }

    private CompactionStrategy? CreateDefaultTruncation()
    {
        if (_options.MaxHistoryMessages <= 0)
        {
            return null;
        }

        return new TruncationCompactionStrategy(
            CompactionTriggers.MessagesExceed(_options.MaxHistoryMessages),
            minimumPreservedGroups: 4,
            target: null);
    }

    private CompactionStrategy CreateSlidingWindow(ContextPolicy policy)
    {
        CompactionTrigger trigger = policy.MaxTokens > 0
            ? CompactionTriggers.TokensExceed(policy.MaxTokens)
            : CompactionTriggers.MessagesExceed(Math.Max(1, _options.MaxHistoryMessages));
        return new SlidingWindowCompactionStrategy(
            trigger,
            Math.Max(1, policy.PreserveRecentTurns),
            target: null);
    }

    private static CompactionStrategy CreateSummarization(ContextPolicy policy, IChatClient chatClient)
    {
        int maxTokens = policy.MaxTokens > 0 ? policy.MaxTokens : 8_000;
        return new SummarizationCompactionStrategy(
            chatClient,
            CompactionTriggers.TokensExceed(maxTokens),
            minimumPreservedGroups: Math.Max(4, policy.PreserveRecentTurns * 2),
            summarizationPrompt: null,
            target: null);
    }
}
