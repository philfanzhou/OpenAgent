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
    private readonly ILoggerFactory _loggerFactory;
    private readonly IFileAssetService _fileService;

    public ConversationHistoryFactory(
        IConversationLock conversationLock,
        ConversationSessionStore store,
        IOptions<ConversationStoreOptions> options,
        FileAssetExecutionContext fileExecution,
        ILogger<PlatformChatHistory> logger,
        ILoggerFactory loggerFactory,
        IFileAssetService fileService)
    {
        _conversationLock = conversationLock;
        _store = store;
        _options = options.Value;
        _fileExecution = fileExecution;
        _logger = logger;
        _loggerFactory = loggerFactory;
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
            request.TraceId);
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
            request.TraceId);
        await _store.OpenAsync(
            context,
            agentId,
            request.Query,
            cancellationToken).ConfigureAwait(false);
    }

    internal AIContextProvider? CreateCompaction(
        ContextPolicy? policy,
        IChatClient chatClient,
        ConversationContext context)
    {
        CompactionStrategy? strategy = CreateStrategy(
            policy,
            chatClient,
            force: false,
            out CompactionTrigger trigger);
        if (strategy == null)
        {
            return null;
        }

        string strategyName = ResolveStrategyName(policy);
        var audited = new AuditedCompactionStrategy(
            strategy,
            trigger,
            strategyName,
            "Automatic",
            context,
            _store.Store,
            _loggerFactory.CreateLogger<AuditedCompactionStrategy>(),
            recordUnchanged: false);
        return new CompactionProvider(audited);
    }

    internal CompactionStrategy? CreateStrategy(
        ContextPolicy? policy,
        IChatClient chatClient,
        bool force,
        out CompactionTrigger trigger)
    {
        trigger = ResolveTrigger(policy, force);
        return policy?.Strategy.ToLowerInvariant() switch
        {
            "summarize" => CreateSummarization(policy, chatClient, trigger),
            "sliding_window" => CreateSlidingWindow(policy, trigger),
            "none" => CreateDefaultTruncation(force, trigger),
            null => CreateDefaultTruncation(force, trigger),
            _ => CreateDefaultTruncation(force, trigger)
        };
    }

    internal static string ResolveStrategyName(ContextPolicy? policy) =>
        policy?.Strategy.ToLowerInvariant() switch
        {
            "summarize" => "summarization",
            "sliding_window" => "sliding_window",
            _ => "truncation"
        };

    private CompactionTrigger ResolveTrigger(ContextPolicy? policy, bool force)
    {
        if (force)
        {
            return CompactionTriggers.Always;
        }

        return policy?.Strategy.ToLowerInvariant() switch
        {
            "summarize" => CompactionTriggers.TokensExceed(
                policy.MaxTokens > 0 ? policy.MaxTokens : 8_000),
            "sliding_window" when policy.MaxTokens > 0 =>
                CompactionTriggers.TokensExceed(policy.MaxTokens),
            _ => CompactionTriggers.MessagesExceed(Math.Max(1, _options.MaxHistoryMessages))
        };
    }

    private CompactionStrategy? CreateDefaultTruncation(
        bool force,
        CompactionTrigger trigger)
    {
        if (!force && _options.MaxHistoryMessages <= 0)
        {
            return null;
        }

        return new TruncationCompactionStrategy(
            trigger,
            minimumPreservedGroups: 4,
            target: null);
    }

    private static CompactionStrategy CreateSlidingWindow(
        ContextPolicy policy,
        CompactionTrigger trigger)
    {
        return new SlidingWindowCompactionStrategy(
            trigger,
            Math.Max(1, policy.PreserveRecentTurns),
            target: null);
    }

    private static CompactionStrategy CreateSummarization(
        ContextPolicy policy,
        IChatClient chatClient,
        CompactionTrigger trigger)
    {
        return new SummarizationCompactionStrategy(
            chatClient,
            trigger,
            minimumPreservedGroups: Math.Max(4, policy.PreserveRecentTurns * 2),
            summarizationPrompt: null,
            target: null);
    }
}
