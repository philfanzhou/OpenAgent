using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Conversation;

internal sealed class ConversationHistoryFactory
{
    private readonly IConversationLock _conversationLock;
    private readonly ConversationSessionStore _store;
    private readonly ConversationStoreOptions _options;
    private readonly FileAssetExecutionContext _fileExecution;
    private readonly ILogger<PlatformChatHistory> _logger;
    private readonly IFileAssetService _fileService;

    public ConversationHistoryFactory(
        IConversationLock conversationLock,
        ConversationSessionStore store,
        IOptions<ConversationStoreOptions> options,
        FileAssetExecutionContext fileExecution,
        ILogger<PlatformChatHistory> logger,
        IFileAssetService fileService)
    {
        _conversationLock = conversationLock;
        _store = store;
        _options = options.Value;
        _fileExecution = fileExecution;
        _logger = logger;
        _fileService = fileService;
    }

    internal PlatformChatHistory Create(
        string agentId,
        string modelId,
        AgentRequest request,
        IAgentUserContext user,
        IReadOnlyList<FileAssetContent> files)
    {
        ConversationContext context = new(
            request.ConversationId,
            user.TenantId,
            user.UserId,
            agentId,
            request.TraceId,
            request.ConversationType);
        return new PlatformChatHistory(
            context,
            agentId,
            modelId,
            request.Query,
            files.Select(item => item.Asset).ToList().AsReadOnly(),
            _fileExecution,
            _conversationLock,
            _store,
            _logger,
            _fileService);
    }

    internal async Task EnsureConversationAsync(
        string agentId,
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        ConversationContext context = new(
            request.ConversationId,
            user.TenantId,
            user.UserId,
            agentId,
            request.TraceId,
            request.ConversationType);
        await _store.OpenAsync(
            context,
            agentId,
            request.Query,
            cancellationToken).ConfigureAwait(false);
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
