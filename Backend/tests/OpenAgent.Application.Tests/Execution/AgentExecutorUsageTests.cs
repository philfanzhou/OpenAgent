using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Capabilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Conversation.Store;
using OpenAgent.Application.Execution;
using OpenAgent.Application.Exten;
using OpenAgent.Application.ModelProviders;
using OpenAgent.Application.Observability;
using OpenAgent.Application.Runtime.Agent;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Application.Tooling.Abstractions;
using Xunit;

namespace OpenAgent.Application.Tests.Runtime;

public class AgentExecutorUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ActualRuntimeIdentity_PersistsOnSuccessAndFailure(bool fails)
    {
        var driver = new ScriptedRuntimeDriver(
        [
            new AgentStreamEvent
            {
                Type = AgentStreamEventType.RuntimeStarted,
                Runtime = new("OpenCode", "saved-runtime", "Local runtime", "opencode", "1.2.3")
            },
            Content("response")
        ], fails ? new InvalidOperationException("runtime failed") : null);
        await using TestRuntime runtime = CreateRuntime(driver);
        Task execution = runtime.Executor.ExecuteAsync(CreateRequest("runtime-identity"), User, CancellationToken.None);
        if (fails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        }
        else
        {
            await execution;
        }
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "runtime-identity"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");
        Assert.Equal("OpenCode", assistant.Metadata?.Extensions?["runtimeProvider"]);
        Assert.Equal("saved-runtime", assistant.Metadata?.Extensions?["runtimeProfileId"]);
        Assert.Equal("1.2.3", assistant.Metadata?.Extensions?["runtimeAgentVersion"]);
    }

    [Fact]
    public async Task ExecuteAsync_CurrentUserProfileFunction_IsPassedToRuntimeDriver()
    {
        var driver = new ScriptedRuntimeDriver([Content("provider response")]);
        await using TestRuntime runtime = CreateRuntime(driver);

        await runtime.Executor.ExecuteAsync(
            CreateRequest("profile-function-conversation"),
            User,
            CancellationToken.None);

        RuntimeInvocation invocation = Assert.Single(driver.Invocations);
        Assert.Contains(invocation.Tools, tool => tool.Name == "get_current_user_profile");
    }

    [Fact]
    public async Task ExecuteAsync_DriverReturnsUsage_MapsAndPersistsActualCounts()
    {
        TokenUsage providerUsage = CreateUsage();
        var driver = new ScriptedRuntimeDriver(
        [
            Content("provider response"),
            new AgentStreamEvent { Type = AgentStreamEventType.Usage, ModelId = "configured-model", Usage = providerUsage }
        ]);
        await using TestRuntime runtime = CreateRuntime(driver);

        AgentResponse response = await runtime.Executor.ExecuteAsync(
            CreateRequest("non-stream-conversation"),
            User,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "non-stream-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        AssertUsage(response.TokenUsage);
        AssertUsage(assistant.TokenUsage);
        Assert.Equal("configured-model", response.ModelId);
        Assert.Equal("configured-model", assistant.ModelId);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ToolCallWithNullArguments_NormalizedAndPersisted()
    {
        // 部分 harness 回放的调用参数可能为 null：持久化与播报必须统一成空字典。
        var driver = new ScriptedRuntimeDriver(
        [
            new AgentStreamEvent { Type = AgentStreamEventType.ToolCall, ToolCallId = "call-1", ToolName = "get_current_user_profile", ToolArguments = null },
            new AgentStreamEvent { Type = AgentStreamEventType.ToolResult, ToolCallId = "call-1", Content = "profile text" },
            Content("done")
        ]);
        await using TestRuntime runtime = CreateRuntime(driver);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("null-arguments-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        AgentStreamEvent call = Assert.Single(events, item => item.Type == AgentStreamEventType.ToolCall);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "null-arguments-conversation"));
        ConversationMessage toolRow = Assert.Single(record.Messages, message => message.Role == "tool");
        Assert.Equal("call-1", toolRow.ToolCallId);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ToolEvents_EmitsToolResultAfterItsCall()
    {
        var driver = new ScriptedRuntimeDriver(
        [
            new AgentStreamEvent { Type = AgentStreamEventType.ToolCall, ToolCallId = "call-1", ToolName = "get_current_user_profile" },
            new AgentStreamEvent { Type = AgentStreamEventType.ToolResult, ToolCallId = "call-1", Content = "profile text" },
            Content("tool finished")
        ]);
        await using TestRuntime runtime = CreateRuntime(driver);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("tool-result-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        AgentStreamEvent call = Assert.Single(events, item => item.Type == AgentStreamEventType.ToolCall);
        Assert.Equal("get_current_user_profile", call.ToolName);
        AgentStreamEvent result = Assert.Single(events, item => item.Type == AgentStreamEventType.ToolResult);
        Assert.Equal("call-1", result.ToolCallId);
        Assert.NotNull(result.Content);
        Assert.True(events.IndexOf(result) > events.IndexOf(call), "Tool result must be streamed after its call");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_TerminalUsage_EmitsTerminalEventAndPersistsCounts()
    {
        TokenUsage providerUsage = CreateUsage();
        var driver = new ScriptedRuntimeDriver(
        [
            Content("streamed response"),
            new AgentStreamEvent { Type = AgentStreamEventType.Usage, ModelId = "configured-model", Usage = providerUsage }
        ]);
        await using TestRuntime runtime = CreateRuntime(driver);

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
        Assert.Equal("configured-model", terminal.ModelId);
        Assert.Equal("configured-model", assistant.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_DriverOmitsUsage_ReturnsAndPersistsUnavailable()
    {
        var driver = new ScriptedRuntimeDriver([Content("provider response")]);
        await using TestRuntime runtime = CreateRuntime(driver);

        AgentResponse response = await runtime.Executor.ExecuteAsync(
            CreateRequest("missing-usage-conversation"),
            User,
            CancellationToken.None);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "missing-usage-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        Assert.Null(response.TokenUsage);
        Assert.Null(assistant.TokenUsage);
        Assert.Equal("configured-model", response.ModelId);
        Assert.Equal("configured-model", assistant.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_DriverFails_DoesNotPersistFabricatedUsage()
    {
        var driver = new ScriptedRuntimeDriver(
            [Content("partial response")],
            new InvalidOperationException("provider failed"));
        await using TestRuntime runtime = CreateRuntime(driver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Executor.ExecuteAsync(
            CreateRequest("failed-conversation"),
            User,
            CancellationToken.None));
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "failed-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        Assert.Equal(ConversationStatus.Failed, record.Status);
        Assert.Equal("Failed", assistant.Metadata?.ExecutionStatus);
        Assert.DoesNotContain(record.Messages, message => message.TokenUsage != null);
    }

    [Fact]
    public async Task ExecuteAsync_DriverIsCancelled_PersistsOneUnavailableResponse()
    {
        var driver = new ScriptedRuntimeDriver(
            [Content("partial response")],
            new OperationCanceledException("provider cancelled"));
        await using TestRuntime runtime = CreateRuntime(driver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Executor.ExecuteAsync(
            CreateRequest("cancelled-conversation"),
            User,
            CancellationToken.None));
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "cancelled-conversation"));
        ConversationMessage assistant = Assert.Single(record.Messages, message => message.Role == "assistant");

        Assert.Equal(ConversationStatus.Cancelled, record.Status);
        Assert.Equal("Cancelled", assistant.Metadata?.ExecutionStatus);
        Assert.Null(assistant.TokenUsage);
    }

    private static AgentStreamEvent Content(string text) => new()
    {
        Type = AgentStreamEventType.Content,
        Content = text
    };

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "hello",
        AgentId = "test-agent",
        LlmProfileId = "test-model",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };

    private static TokenUsage CreateUsage() => new()
    {
        PromptTokens = 21,
        CompletionTokens = 8,
        TotalTokens = 29,
        CachedInputTokens = 5,
        ReasoningTokens = 3
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

    internal static TestRuntime CreateRuntime(IAgentRuntimeDriver driver) =>
        CreateRuntime(driver, configure: null);

    internal static TestRuntime CreateRuntime(
        IAgentRuntimeDriver driver,
        OpenAgent.Contracts.Capabilities.ICapabilitySource? extraSource = null,
        int maxTurns = 2,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserContext>(new TestCurrentUserContext());
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<IFileAssetRepository, EmptyFileAssetRepository>();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddSingleton(configuration);
        services.AddAgentApplication(configuration);
        // 生产默认装配：RuntimeAgentExecutor + 注入的驱动替身。
        services.AddSingleton(driver);
        if (extraSource != null)
        {
            services.AddSingleton(extraSource);
        }
        services.RemoveAll<IAgentRuntimeResolver>();
        services.RemoveAll<AgentRuntimeResolver>();
        services.AddSingleton<IAgentRuntimeResolver>(new StaticRuntimeResolver(maxTurns));
        services.RemoveAll<IAgentChatClientFactory>();
        services.AddSingleton<IAgentChatClientFactory>(new FakeChatClientFactory(FakeChatClient.Unused));
        configure?.Invoke(services);

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

    /// <summary>摘要/兼容客户端占位：聊天推理只走驱动，任何调用都是测试失败。</summary>
    private sealed class FakeChatClient : IChatClient
    {
        internal static readonly FakeChatClient Unused = new();

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Chat inference must run through the runtime driver.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Chat inference must run through the runtime driver.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public string UserId => User.UserId;
        public string? TenantId => User.TenantId;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private sealed class StaticRuntimeResolver(int maxTurns) : IAgentRuntimeResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            string llmProfileId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRuntimeProfile
            {
                AgentId = agentId,
                Config = new AgentConfig { MaxTurns = maxTurns },
                Model = new LlmConfig { ModelId = "configured-model" }
            });
    }

    private sealed class FakeChatClientFactory(IChatClient provider) : IAgentChatClientFactory
    {
        public IChatClient Create(LlmConfig llm, LlmInteractionCapture? capture = null) => provider;

        public IChatClient CreateSummarizationClient(
            LlmConfig llm,
            ContextPolicy? policy,
            LlmInteractionCapture? capture = null) => provider;
    }

    internal sealed class TestRuntime(
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
