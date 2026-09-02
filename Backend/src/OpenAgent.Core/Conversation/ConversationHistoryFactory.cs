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
    private const double AutomaticTriggerRatio = 0.8;
    private const double CompactionTargetRatio = 0.5;
    private const double SummaryBudgetRatio = 0.2;
    private const int MinimumSummaryTokens = 32;
    private const string SummarizationPrompt = """
        You are the conversation context compressor. This is a dedicated compression call, not a user-facing reply.
        Convert only the older conversation messages supplied to this call into compact continuation state for a future assistant.
        Preserve the user's active intent, decisions, constraints, preferences, unresolved questions, confirmed facts, relevant identifiers,
        configuration values, and concise tool/MCP outcomes or errors. Keep conclusions from reasoning, not verbose reasoning traces.
        Remove greetings, repetition, filler, superseded details, and raw tool output that is no longer needed.
        Do not answer the user, continue the conversation, invent facts, or mention this compression instruction.
        Return only the summary. Prefer these short sections and omit empty ones:
        - Task and intent
        - Decisions and constraints
        - Confirmed state and tool outcomes
        - Open items and next action
        """;

    private readonly IConversationLock _conversationLock;
    private readonly ConversationSessionStore _store;
    private readonly ConversationStoreOptions _options;
    private readonly FileAssetExecutionContext _fileExecution;
    private readonly ILogger<PlatformChatHistory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IFileAssetService _fileService;
    private readonly FileAssetOptions _fileOptions;

    public ConversationHistoryFactory(
        IConversationLock conversationLock,
        ConversationSessionStore store,
        IOptions<ConversationStoreOptions> options,
        FileAssetExecutionContext fileExecution,
        ILogger<PlatformChatHistory> logger,
        ILoggerFactory loggerFactory,
        IFileAssetService fileService,
        IOptions<FileAssetOptions> fileOptions)
    {
        _conversationLock = conversationLock;
        _store = store;
        _options = options.Value;
        _fileExecution = fileExecution;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _fileService = fileService;
        _fileOptions = fileOptions.Value;
    }

    internal PlatformChatHistory Create(
        string agentId,
        string modelId,
        AgentRequest request,
        IAgentUserContext user,
        IReadOnlyList<FileAsset> files,
        bool supportsMultimodal)
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
            files.ToList().AsReadOnly(),
            _fileExecution,
            _conversationLock,
            _store,
            _logger,
            _fileService,
            supportsMultimodal,
            _fileOptions.MaxInlineImageBytes,
            _fileOptions.MaxInlineImageCount);
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

    internal AIContextProvider CreateCompaction(
        ContextPolicy? policy,
        IChatClient summarizationClient,
        string? tenantId,
        string? conversationId)
    {
        SummarizationCompactionStrategy strategy = CreateStrategy(
            policy,
            summarizationClient,
            force: false,
            out CompactionTrigger trigger);
        var audited = new AuditedCompactionStrategy(
            strategy,
            trigger,
            "Automatic",
            tenantId,
            conversationId,
            _store.Store,
            _loggerFactory.CreateLogger<AuditedCompactionStrategy>(),
            recordUnchanged: false);
        return new CompactionProvider(audited);
    }

    internal SummarizationCompactionStrategy CreateStrategy(
        ContextPolicy? policy,
        IChatClient summarizationClient,
        bool force,
        out CompactionTrigger trigger)
    {
        trigger = ResolveTrigger(policy, force);
        return CreateSummarization(policy, summarizationClient, trigger, force);
    }

    private CompactionTrigger ResolveTrigger(ContextPolicy? policy, bool force)
    {
        if (force)
        {
            // Manual compaction is an explicit user request and may run at any
            // context size. Result auditing still rejects summaries that expand
            // the context or fail to save a meaningful number of tokens.
            return CompactionTriggers.Always;
        }

        return CompactionTriggers.TokensExceed(ResolveAutomaticTokenThreshold(policy));
    }

    internal int ResolveAutomaticTokenThreshold(ContextPolicy? policy)
    {
        // A configured fixed threshold wins over the ratio heuristic, so deployments can
        // pin the automatic trigger regardless of per-agent context policies.
        if (_options.AutomaticCompactionTokenThreshold is > 0)
        {
            return _options.AutomaticCompactionTokenThreshold.Value;
        }

        int contextTokens = policy?.MaxTokens > 0
            ? policy.MaxTokens
            : Math.Max(1, _options.DefaultModelContextTokens);

        // Automatic compaction starts at 80% of the available model context.
        return Math.Max(1, (int)Math.Floor(contextTokens * AutomaticTriggerRatio));
    }

    internal int ResolveCompactionTargetTokens(ContextPolicy? policy)
    {
        int contextTokens = policy?.MaxTokens > 0
            ? policy.MaxTokens
            : Math.Max(1, _options.DefaultModelContextTokens);

        // Keep a reserve for the next user turn and model response. Tune this with
        // the real model context limit once provider limits are exposed by the runtime.
        return Math.Max(1, (int)Math.Floor(contextTokens * CompactionTargetRatio));
    }

    internal int ResolveSummaryTokenBudget(ContextPolicy? policy)
    {
        int contextTokens = policy?.MaxTokens > 0
            ? policy.MaxTokens
            : Math.Max(1, _options.DefaultModelContextTokens);
        int proportionalBudget = Math.Max(
            MinimumSummaryTokens,
            (int)Math.Floor(contextTokens * SummaryBudgetRatio));
        int configuredBudget = policy?.SummarizeOptions?.MaxSummaryTokens ?? 512;

        // MaxSummaryTokens is an upper bound, not a fixed target. A fixed 512-token
        // summary is too large for the temporary 1,000-token fallback context.
        return Math.Max(1, Math.Min(proportionalBudget, configuredBudget));
    }

    private SummarizationCompactionStrategy CreateSummarization(
        ContextPolicy? policy,
        IChatClient chatClient,
        CompactionTrigger trigger,
        bool force)
    {
        int targetTokens = ResolveCompactionTargetTokens(policy);
        int summaryBudget = ResolveSummaryTokenBudget(policy);
        int minimumPreservedGroups = force
            ? 0
            : Math.Max(1, policy?.PreserveRecentTurns ?? 2);
        string prompt = $"{SummarizationPrompt.Trim()}\nHARD LIMIT: the summary must not exceed {summaryBudget} tokens.";
        return new SummarizationCompactionStrategy(
            new OutputTokenLimitedChatClient(chatClient, summaryBudget),
            trigger,
            minimumPreservedGroups,
            summarizationPrompt: prompt,
            target: force ? null : CompactionTriggers.TokensBelow(targetTokens));
    }
}
