using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Conversation.Compaction;
using OpenAgent.Application.Conversation.Store;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.ModelProviders;

namespace OpenAgent.Application.Conversation.History;

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

    private readonly ConversationSessionStore _store;
    private readonly ConversationStoreOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;

    public ConversationHistoryFactory(
        ConversationSessionStore store,
        IOptions<ConversationStoreOptions> options,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _store = store;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _services = services;
    }

    internal PlatformChatHistory Create(
        TurnContext turn,
        string modelId,
        string input,
        IReadOnlyList<FileAsset> files,
        bool supportsMultimodal)
    {
        ConversationContext context = turn.ToConversationContext();
        return ActivatorUtilities.CreateInstance<PlatformChatHistory>(_services, new PlatformChatHistoryContext(
            context,
            modelId,
            input,
            files.ToList().AsReadOnly(),
            supportsMultimodal));
    }

    internal async Task EnsureConversationAsync(
        TurnContext turn,
        string input,
        CancellationToken cancellationToken)
    {
        ConversationContext context = turn.ToConversationContext();
        await _store.OpenAsync(
            context,
            turn.AgentId ?? string.Empty,
            input,
            cancellationToken).ConfigureAwait(false);
    }

    internal AuditedCompactionStrategy CreateCompaction(
        int contextTokens,
        ContextPolicy? policy,
        IChatClient summarizationClient,
        TurnContext turn)
    {
        SummarizationCompactionStrategy strategy = CreateStrategy(
            contextTokens,
            policy,
            summarizationClient,
            force: false,
            out CompactionTrigger trigger);
        return new AuditedCompactionStrategy(
            strategy,
            trigger,
            "Automatic",
            turn.TenantId,
            turn.ConversationId,
            _store.Store,
            _loggerFactory.CreateLogger<AuditedCompactionStrategy>(),
            recordUnchanged: false);
    }

    internal SummarizationCompactionStrategy CreateStrategy(
        int contextTokens,
        ContextPolicy? policy,
        IChatClient summarizationClient,
        bool force,
        out CompactionTrigger trigger)
    {
        trigger = ResolveTrigger(contextTokens, force);
        return CreateSummarization(contextTokens, policy, summarizationClient, trigger, force);
    }

    private CompactionTrigger ResolveTrigger(int contextTokens, bool force)
    {
        if (force)
        {
            // Manual compaction is an explicit user request and may run at any
            // context size. Result auditing still rejects summaries that expand
            // the context or fail to save a meaningful number of tokens.
            return CompactionTriggers.Always;
        }

        return CompactionTriggers.TokensExceed(ResolveAutomaticTokenThreshold(contextTokens));
    }

    internal int ResolveAutomaticTokenThreshold(int contextTokens)
    {
        contextTokens = contextTokens > 0
            ? contextTokens
            : Math.Max(1, _options.DefaultModelContextTokens);

        // Automatic compaction starts at 80% of the available model context.
        return Math.Max(1, (int)Math.Floor(contextTokens * AutomaticTriggerRatio));
    }

    internal int ResolveCompactionTargetTokens(int contextTokens)
    {
        contextTokens = contextTokens > 0
            ? contextTokens
            : Math.Max(1, _options.DefaultModelContextTokens);

        // Keep a reserve for the next user turn and model response. Tune this with
        // the real model context limit once provider limits are exposed by the runtime.
        return Math.Max(1, (int)Math.Floor(contextTokens * CompactionTargetRatio));
    }

    internal int ResolveSummaryTokenBudget(int contextTokens, ContextPolicy? policy)
    {
        contextTokens = contextTokens > 0
            ? contextTokens
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
        int contextTokens,
        ContextPolicy? policy,
        IChatClient chatClient,
        CompactionTrigger trigger,
        bool force)
    {
        int targetTokens = ResolveCompactionTargetTokens(contextTokens);
        int summaryBudget = ResolveSummaryTokenBudget(contextTokens, policy);
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
