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
    public async Task ExecuteAsync_ConversationOverride_PersistsAndAppliesToFollowingRequest()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response")));
        await using TestRuntime runtime = CreateRuntime(provider);
        AgentRequest first = new()
        {
            Query = "hello",
            AgentId = "test-agent",
            ConversationId = "model-conversation",
            TraceId = "trace-model-conversation",
            ConversationModelOverride = new LlmModelSelection
            {
                Provider = "provider-1",
                ModelId = "conversation-model"
            },
            UpdateConversationModelOverride = true
        };

        await runtime.Executor.ExecuteAsync(first, User, CancellationToken.None);
        await runtime.Executor.ExecuteAsync(
            CreateRequest("model-conversation"),
            User,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "model-conversation"));
        ConversationMessage[] userMessages = record.Messages
            .Where(message => message.Role == "user")
            .ToArray();

        Assert.Equal("provider-1", record.ModelOverride?.Provider);
        Assert.Equal("conversation-model", record.ModelOverride?.ModelId);
        Assert.Equal(2, userMessages.Length);
        Assert.All(userMessages, message =>
            Assert.Equal("conversation-model", message.Metadata?["ModelId"]));
        Assert.All(userMessages, message =>
            Assert.Equal("Conversation", message.Metadata?["ModelSelectionSource"]));
    }

    [Fact]
    public async Task ExecuteAsync_MessageOverride_PersistsAuditMetadataWithoutChangingConversation()
    {
        var provider = new FakeChatProvider(new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, "provider response")));
        await using TestRuntime runtime = CreateRuntime(provider);
        AgentRequest request = new()
        {
            Query = "hello",
            AgentId = "test-agent",
            ConversationId = "message-model-conversation",
            TraceId = "trace-message-model-conversation",
            MessageModelOverride = new LlmModelSelection
            {
                Provider = "provider-1",
                ModelId = "message-model"
            }
        };

        await runtime.Executor.ExecuteAsync(request, User, CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "message-model-conversation"));
        ConversationMessage userMessage = Assert.Single(
            record.Messages,
            message => message.Role == "user");

        Assert.Null(record.ModelOverride);
        Assert.Equal("provider-1", userMessage.Metadata?["ModelProvider"]);
        Assert.Equal("message-model", userMessage.Metadata?["ModelId"]);
        Assert.Equal("Message", userMessage.Metadata?["ModelSelectionSource"]);
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "hello",
        AgentId = "test-agent",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
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

    private static TestRuntime CreateRuntime(IChatClient provider)
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
        services.AddSingleton<IAgentRuntimeResolver>(new StaticRuntimeResolver());
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

    private sealed class StaticRuntimeResolver : IAgentRuntimeResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRuntimeProfile
            {
                AgentId = agentId,
                Config = new AgentConfig { MaxTurns = 2 },
                Model = new LlmConfig { ModelId = "configured-model" }
            });

        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            IAgentUserContext userContext,
            LlmModelSelection? modelOverride,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRuntimeProfile
            {
                AgentId = agentId,
                Config = new AgentConfig { MaxTurns = 2 },
                Model = new LlmConfig
                {
                    Provider = modelOverride?.Provider ?? "configured-provider",
                    ModelId = modelOverride?.ModelId ?? "configured-model"
                }
            });
    }

    private sealed class FakeChatClientFactory(IChatClient provider) : IAgentChatClientFactory
    {
        public IChatClient Create(LlmConfig llm) => provider;
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
