using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Exten;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class AgentExecutorUsageTests
{
    [Fact]
    public async Task ExecuteAsync_CurrentUserProfileFunction_IsPassedToModelClient()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response")));
        await using TestRuntime runtime = CreateRuntime(provider);

        await runtime.Executor.ExecuteAsync(
            CreateRequest("profile-function-conversation"),
            User,
            CancellationToken.None);

        Assert.Contains(
            Assert.IsAssignableFrom<IList<AITool>>(provider.LastOptions?.Tools),
            tool => tool is AIFunction function
                && function.Name == "get_current_user_profile");
    }

    [Fact]
    public async Task ExecuteAsync_ProviderReturnsUsage_MapsAndPersistsActualCounts()
    {
        UsageDetails providerUsage = CreateUsage();
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response"))
        {
            Usage = providerUsage,
            ModelId = "provider-model-2026"
        });
        await using TestRuntime runtime = CreateRuntime(provider);

        AgentResponse response = await runtime.Executor.ExecuteAsync(
            CreateRequest("non-stream-conversation"),
            User,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "non-stream-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        AssertUsage(response.TokenUsage);
        AssertUsage(assistant.TokenUsage);
        Assert.Equal("provider-model-2026", response.ModelId);
        Assert.Equal("provider-model-2026", assistant.ModelId);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_TerminalProviderUsage_EmitsTerminalEventAndPersistsCounts()
    {
        UsageDetails providerUsage = CreateUsage();
        var provider = new FakeChatProvider(
        [
            new ChatResponseUpdate(ChatRole.Assistant, "streamed response")
            {
                ModelId = "provider-stream-model"
            },
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(providerUsage)])
            {
                ModelId = "provider-stream-model"
            }
        ]);
        await using TestRuntime runtime = CreateRuntime(provider);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("stream-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "stream-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");
        AgentStreamEvent terminal = Assert.Single(events, item => item.Type == AgentStreamEventType.Usage);

        AssertUsage(terminal.Usage);
        AssertUsage(assistant.TokenUsage);
        Assert.Equal("provider-stream-model", terminal.ModelId);
        Assert.Equal("provider-stream-model", assistant.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderOmitsUsage_ReturnsAndPersistsUnavailable()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response"))
        {
            ModelId = "provider-model"
        });
        await using TestRuntime runtime = CreateRuntime(provider);

        AgentResponse response = await runtime.Executor.ExecuteAsync(
            CreateRequest("missing-usage-conversation"),
            User,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "missing-usage-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        Assert.Null(response.TokenUsage);
        Assert.Null(assistant.TokenUsage);
        Assert.Equal("provider-model", assistant.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderFails_DoesNotPersistFabricatedUsage()
    {
        var provider = new FakeChatProvider(new InvalidOperationException("provider failed"));
        await using TestRuntime runtime = CreateRuntime(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Executor.ExecuteAsync(
            CreateRequest("failed-conversation"),
            User,
            CancellationToken.None));
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "failed-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        Assert.Equal(ConversationStatus.Failed, record.Status);
        Assert.Equal("Failed", assistant.Metadata?["ExecutionStatus"]);
        Assert.DoesNotContain(record.Messages, message => message.TokenUsage != null);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderIsCancelled_PersistsOneUnavailableResponse()
    {
        var provider = new FakeChatProvider(new OperationCanceledException("provider cancelled"));
        await using TestRuntime runtime = CreateRuntime(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Executor.ExecuteAsync(
            CreateRequest("cancelled-conversation"),
            User,
            CancellationToken.None));
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "cancelled-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        Assert.Equal(ConversationStatus.Cancelled, record.Status);
        Assert.Equal("Cancelled", assistant.Metadata?["ExecutionStatus"]);
        Assert.Null(assistant.TokenUsage);
    }

    [Fact]
    public async Task ExecuteAsync_RequestTokenOverrides_AppliesProviderOptionAndPersistsEffectiveValues()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response"))
        {
            Usage = CreateUsage()
        });
        AgentRuntimeProfile profile = CreateProfile(
            agentContext: 80,
            agentOutput: 10,
            modelContext: 100,
            modelOutput: 20);
        await using TestRuntime runtime = CreateRuntime(provider, profile);
        AgentRequest request = CreateRequest("token-override-conversation", 90, 15);

        await runtime.Executor.ExecuteAsync(request, User, CancellationToken.None);

        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "token-override-conversation"));
        ConversationMessage user = Assert.Single(record.Messages, message => message.Role == "user");
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");
        Assert.Equal(15, provider.LastOptions?.MaxOutputTokens);
        Assert.Equal("100", user.Metadata?["LlmModelContextWindowTokens"]);
        Assert.Equal("80", user.Metadata?["LlmAgentContextWindowTokens"]);
        Assert.Equal("90", user.Metadata?["LlmRequestContextWindowTokens"]);
        Assert.Equal("90", user.Metadata?["LlmEffectiveContextWindowTokens"]);
        Assert.Equal("15", user.Metadata?["LlmEffectiveMaxOutputTokens"]);
        Assert.Equal("True", user.Metadata?["LlmMaxOutputTokensApplied"]);
        AssertUsage(assistant.TokenUsage);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderDoesNotSupportOutputParameter_UsesModelFallbackWithoutSendingIt()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response")));
        AgentRuntimeProfile profile = CreateProfile(
            agentContext: null,
            agentOutput: null,
            modelContext: 100,
            modelOutput: 20,
            supportsMaxOutputTokens: false);
        await using TestRuntime runtime = CreateRuntime(provider, profile);

        await runtime.Executor.ExecuteAsync(
            CreateRequest("unsupported-output-conversation"),
            User,
            CancellationToken.None);

        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "unsupported-output-conversation"));
        ConversationMessage user = Assert.Single(record.Messages, message => message.Role == "user");
        Assert.Null(provider.LastOptions?.MaxOutputTokens);
        Assert.Equal("20", user.Metadata?["LlmEffectiveMaxOutputTokens"]);
        Assert.Equal("False", user.Metadata?["LlmMaxOutputTokensSupported"]);
        Assert.Equal("False", user.Metadata?["LlmMaxOutputTokensApplied"]);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderDoesNotSupportRequestOverride_FailsBeforeProviderCall()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response")));
        AgentRuntimeProfile profile = CreateProfile(
            agentContext: null,
            agentOutput: null,
            modelContext: 100,
            modelOutput: 20,
            supportsMaxOutputTokens: false);
        await using TestRuntime runtime = CreateRuntime(provider, profile);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            runtime.Executor.ExecuteAsync(
                CreateRequest("unsupported-output-request", maxOutputTokens: 10),
                User,
                CancellationToken.None));

        Assert.Equal(AgentErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_HistoryExceedsEffectiveContext_CompactsProviderInputAndPreservesAuditHistory()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response")));
        AgentRuntimeProfile profile = CreateProfile(
            agentContext: 100,
            agentOutput: 20,
            modelContext: 100,
            modelOutput: 20,
            contextPolicy: new ContextPolicy
            {
                MaxTokens = 60,
                PreserveRecentTurns = 1
            });
        await using TestRuntime runtime = CreateRuntime(provider, profile);
        List<ConversationMessage> history = Enumerable.Range(1, 8)
            .Select(index => new ConversationMessage
            {
                MessageId = $"message-{index}",
                Sequence = index,
                Role = index % 2 == 0 ? "assistant" : "user",
                Content = new string((char)('a' + index), 400)
            })
            .ToList();
        int historyCount = history.Count;
        await runtime.Store.CreateAsync(new ConversationRecord
        {
            ConversationId = "compaction-conversation",
            TenantId = "tenant-1",
            UserId = "user-1",
            AgentId = "test-agent",
            MessageCount = history.Count,
            Messages = history
        });

        await runtime.Executor.ExecuteAsync(
            CreateRequest("compaction-conversation"),
            User,
            CancellationToken.None);

        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "compaction-conversation"));
        Assert.True(provider.LastMessages.Count < historyCount + 1);
        Assert.Equal(historyCount + 2, record.Messages.Count);
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(
        string conversationId,
        int? contextWindowTokens = null,
        int? maxOutputTokens = null) => new()
    {
        Query = "hello",
        AgentId = "test-agent",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}",
        ContextWindowTokens = contextWindowTokens,
        MaxOutputTokens = maxOutputTokens
    };

    private static UsageDetails CreateUsage() => new()
    {
        InputTokenCount = 21,
        OutputTokenCount = 8,
        TotalTokenCount = 29,
        CachedInputTokenCount = 5,
        ReasoningTokenCount = 3,
        AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["billing_units"] = 99 }
    };

    private static void AssertUsage(TokenUsage? usage)
    {
        TokenUsage actual = Assert.IsType<TokenUsage>(usage);
        Assert.Equal(21, actual.PromptTokens);
        Assert.Equal(8, actual.CompletionTokens);
        Assert.Equal(29, actual.TotalTokens);
        Assert.Equal(5, actual.CachedInputTokens);
        Assert.Equal(3, actual.ReasoningTokens);
    }

    private static TestRuntime CreateRuntime(
        IChatClient provider,
        AgentRuntimeProfile? profile = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserContext>(new TestCurrentUserContext());
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<IFileAssetRepository, EmptyFileAssetRepository>();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddSingleton(configuration);
        services.AddAgentCore(configuration);
        services.RemoveAll<IAgentRuntimeResolver>();
        services.RemoveAll<AgentRuntimeResolver>();
        services.AddSingleton<IAgentRuntimeResolver>(new StaticRuntimeResolver(
            profile ?? CreateProfile()));
        services.RemoveAll<IAgentChatClientFactory>();
        services.AddSingleton<IAgentChatClientFactory>(new FakeChatClientFactory(provider));

        ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        return new TestRuntime(
            serviceProvider,
            scope,
            scope.ServiceProvider.GetRequiredService<AgentExecutor>(),
            Assert.IsType<InMemoryConversationStore>(
                scope.ServiceProvider.GetRequiredService<IConversationStore>()));
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public string UserId => User.UserId;
        public string? TenantId => User.TenantId;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private static AgentRuntimeProfile CreateProfile(
        int? agentContext = null,
        int? agentOutput = null,
        int? modelContext = null,
        int? modelOutput = null,
        bool supportsMaxOutputTokens = true,
        ContextPolicy? contextPolicy = null) => new()
        {
            AgentId = "test-agent",
            Config = new AgentConfig
            {
                MaxTurns = 2,
                ContextPolicy = contextPolicy,
                Llm = new LlmConfig
                {
                    ContextWindowTokens = agentContext,
                    MaxOutputTokens = agentOutput
                }
            },
            Model = new LlmConfig
            {
                ModelId = "configured-model",
                ContextWindowTokens = agentContext ?? modelContext,
                MaxOutputTokens = agentOutput ?? modelOutput,
                TokenCapabilities = new LlmTokenCapabilities
                {
                    ContextWindowTokens = modelContext,
                    MaxOutputTokens = modelOutput,
                    SupportsMaxOutputTokens = supportsMaxOutputTokens
                }
            }
        };

    private sealed class StaticRuntimeResolver(AgentRuntimeProfile profile) : IAgentRuntimeResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private sealed class FakeChatClientFactory(IChatClient provider) : IAgentChatClientFactory
    {
        public IChatClient Create(LlmConfig llm) => provider;

        public IChatClient CreateSummarizationClient(
            LlmConfig llm,
            ContextPolicy? policy) => provider;
    }

    private sealed class EmptyFileAssetRepository : IFileAssetRepository
    {
        public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult<FileAsset?>(null);
        public Task EnsureConversationReferencesAsync(
            string conversationId,
            IReadOnlyList<string> fileIds,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> IsReferencedAsync(
            string conversationId,
            string fileId,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class TestRuntime(
        ServiceProvider serviceProvider,
        AsyncServiceScope scope,
        AgentExecutor executor,
        InMemoryConversationStore store) : IAsyncDisposable
    {
        internal AgentExecutor Executor { get; } = executor;
        internal InMemoryConversationStore Store { get; } = store;

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }
    }
}
