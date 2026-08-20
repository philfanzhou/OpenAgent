using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Exten;
using OpenAgent.Core.Runtime.Agent;
using Xunit;
using AIChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace OpenAgent.Core.Tests.Approvals;

public sealed class HumanApprovalExecutionTests
{
    [Fact]
    public async Task ApprovedFunction_ResumesSerializedMafSessionAndCompletesConversation()
    {
        await using ApprovalRuntime runtime = CreateRuntime();
        AgentResponse suspended = await runtime.Executor.ExecuteAsync(
            Request("approval-resume"),
            Requester,
            CancellationToken.None);

        Assert.NotNull(suspended.Approval);
        Assert.DoesNotContain(
            "do-not-expose",
            suspended.Approval!.RedactedArgumentsJson,
            StringComparison.Ordinal);
        Assert.Equal(0, runtime.Capability.InvocationCount);
        Assert.Equal(
            ConversationStatus.AwaitingApproval,
            (await runtime.Conversations.GetRecordAsync("tenant-1", "approval-resume"))?.Status);

        HumanApprovalDecisionResult decision = await runtime.Approvals.DecideAsync(
            "tenant-1",
            suspended.Approval!.ApprovalId,
            new HumanApprovalDecisionRequest { Approved = true, Reason = "Reviewed" },
            Approver,
            CancellationToken.None);

        Assert.Equal(1, runtime.Capability.InvocationCount);
        Assert.Equal("do-not-expose", runtime.Capability.LastApiKey);
        Assert.Equal("execution complete", decision.Response?.Content);
        Assert.Equal(
            ConversationStatus.Completed,
            (await runtime.Conversations.GetRecordAsync("tenant-1", "approval-resume"))?.Status);
    }

    [Fact]
    public async Task RejectedFunction_DoesNotExecuteAndCancelsConversation()
    {
        await using ApprovalRuntime runtime = CreateRuntime();
        AgentResponse suspended = await runtime.Executor.ExecuteAsync(
            Request("approval-rejected"),
            Requester,
            CancellationToken.None);

        HumanApprovalDecisionResult decision = await runtime.Approvals.DecideAsync(
            "tenant-1",
            suspended.Approval!.ApprovalId,
            new HumanApprovalDecisionRequest { Approved = false, Reason = "Unsafe" },
            Approver,
            CancellationToken.None);

        Assert.Equal(HumanApprovalStatus.Rejected, decision.Approval.Status);
        Assert.Null(decision.Response);
        Assert.Equal(0, runtime.Capability.InvocationCount);
        Assert.Equal(
            ConversationStatus.Cancelled,
            (await runtime.Conversations.GetRecordAsync("tenant-1", "approval-rejected"))?.Status);
    }

    [Fact]
    public async Task ExpiredFunction_DoesNotExecuteAndCancelsConversation()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-20T10:00:00Z"));
        await using ApprovalRuntime runtime = CreateRuntime(clock);
        AgentResponse suspended = await runtime.Executor.ExecuteAsync(
            Request("approval-expired"),
            Requester,
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            runtime.Approvals.DecideAsync(
                "tenant-1",
                suspended.Approval!.ApprovalId,
                new HumanApprovalDecisionRequest { Approved = true },
                Approver,
                CancellationToken.None));

        Assert.Equal(AgentErrorCode.HumanApprovalTimeout, exception.ErrorCode);
        Assert.Equal(0, runtime.Capability.InvocationCount);
        Assert.Equal(
            ConversationStatus.Cancelled,
            (await runtime.Conversations.GetRecordAsync("tenant-1", "approval-expired"))?.Status);
    }

    [Fact]
    public async Task WithdrawnFunction_DoesNotExecuteAndCancelsConversation()
    {
        await using ApprovalRuntime runtime = CreateRuntime();
        AgentResponse suspended = await runtime.Executor.ExecuteAsync(
            Request("approval-withdrawn"),
            Requester,
            CancellationToken.None);

        HumanApprovalRequest withdrawn = await runtime.Approvals.WithdrawAsync(
            "tenant-1",
            suspended.Approval!.ApprovalId,
            Requester,
            CancellationToken.None);

        Assert.Equal(HumanApprovalStatus.Withdrawn, withdrawn.Status);
        Assert.Equal(0, runtime.Capability.InvocationCount);
        Assert.Equal(
            ConversationStatus.Cancelled,
            (await runtime.Conversations.GetRecordAsync("tenant-1", "approval-withdrawn"))?.Status);
    }

    [Fact]
    public async Task ConcurrentApprovals_ExecuteFunctionOnlyOnce()
    {
        await using ApprovalRuntime runtime = CreateRuntime();
        AgentResponse suspended = await runtime.Executor.ExecuteAsync(
            Request("approval-concurrent"),
            Requester,
            CancellationToken.None);

        Task<bool>[] attempts = Enumerable.Range(0, 12)
            .Select(index => TryApproveAsync(
                runtime.Approvals,
                suspended.Approval!.ApprovalId,
                index))
            .ToArray();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        Assert.Equal(1, runtime.Capability.InvocationCount);
    }

    [Fact]
    public async Task CrossTenantApprover_CannotReadOrDecideApproval()
    {
        await using ApprovalRuntime runtime = CreateRuntime();
        AgentResponse suspended = await runtime.Executor.ExecuteAsync(
            Request("approval-tenant-isolation"),
            Requester,
            CancellationToken.None);
        var foreignApprover = new AgentUserContext
        {
            UserId = "approver-foreign",
            TenantId = "tenant-2",
            Roles = ["ApprovalApprover"],
            IsAuthenticated = true
        };

        await Assert.ThrowsAsync<TenantDataIsolationException>(() =>
            runtime.Approvals.DecideAsync(
                "tenant-1",
                suspended.Approval!.ApprovalId,
                new HumanApprovalDecisionRequest { Approved = true },
                foreignApprover,
                CancellationToken.None));

        Assert.Equal(0, runtime.Capability.InvocationCount);
    }

    private static readonly AgentUserContext Requester = new()
    {
        UserId = "requester-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private static readonly AgentUserContext Approver = new()
    {
        UserId = "approver-1",
        TenantId = "tenant-1",
        Roles = ["ApprovalApprover"],
        IsAuthenticated = true
    };

    private static AgentRequest Request(string conversationId) => new()
    {
        Query = "perform the operation",
        AgentId = "approval-agent",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };

    private static async Task<bool> TryApproveAsync(
        IHumanApprovalService approvals,
        string approvalId,
        int index)
    {
        try
        {
            await approvals.DecideAsync(
                "tenant-1",
                approvalId,
                new HumanApprovalDecisionRequest { Approved = true },
                new AgentUserContext
                {
                    UserId = $"approver-{index}",
                    TenantId = "tenant-1",
                    Roles = ["ApprovalApprover"],
                    IsAuthenticated = true
                });
            return true;
        }
        catch (AgentException exception) when (exception.ErrorCode == AgentErrorCode.Conflict)
        {
            return false;
        }
    }

    private static ApprovalRuntime CreateRuntime(MutableTimeProvider? clock = null)
    {
        var capability = new HighRiskCapabilitySource();
        var provider = new ApprovalChatProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserContext>(new TestCurrentUserContext());
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<IFileAssetRepository, EmptyFileAssetRepository>();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HumanApproval:RequestTimeoutMinutes"] = "1",
                ["HumanApproval:SweepIntervalSeconds"] = "30"
            })
            .Build();
        services.AddSingleton(configuration);
        services.AddAgentCore(configuration);
        services.AddSingleton<ICapabilitySource>(capability);
        services.RemoveAll<IAgentRuntimeResolver>();
        services.RemoveAll<AgentRuntimeResolver>();
        services.AddSingleton<IAgentRuntimeResolver>(new StaticRuntimeResolver());
        services.RemoveAll<IAgentChatClientFactory>();
        services.AddSingleton<IAgentChatClientFactory>(new FakeChatClientFactory(provider));
        if (clock != null)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }

        ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        return new ApprovalRuntime(
            serviceProvider,
            scope,
            scope.ServiceProvider.GetRequiredService<AgentExecutor>(),
            scope.ServiceProvider.GetRequiredService<IHumanApprovalService>(),
            Assert.IsType<InMemoryConversationStore>(
                scope.ServiceProvider.GetRequiredService<IConversationStore>()),
            capability);
    }

    private sealed class HighRiskCapabilitySource : ICapabilitySource
    {
        private int _invocationCount;
        internal int InvocationCount => Volatile.Read(ref _invocationCount);
        internal string? LastApiKey { get; private set; }

        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityDefinition>>(
            [
                new CapabilityDefinition(
                    "dangerous_function",
                    "Performs a high-risk operation.",
                    "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}}}",
                    AgentResourceType.Function,
                    "dangerous_function",
                    (arguments, _) =>
                    {
                        if (arguments.TryGetValue("apiKey", out object? value))
                        {
                            LastApiKey = value is JsonElement element
                                ? element.GetString()
                                : value?.ToString();
                        }
                        Interlocked.Increment(ref _invocationCount);
                        return Task.FromResult("executed");
                    },
                    RequiresHumanApproval: true,
                    ApprovalAction: "execute")
            ]);
    }

    private sealed class ApprovalChatProvider : IChatClient
    {
        public Task<AIChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasResult = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any();
            ChatMessage message = hasResult
                ? new ChatMessage(ChatRole.Assistant, "execution complete")
                : new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call-1",
                        "dangerous_function",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "production",
                            ["apiKey"] = "do-not-expose"
                        })]);
            return Task.FromResult(new AIChatResponse(message)
            {
                ModelId = "approval-model"
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AIChatResponse response = await GetResponseAsync(
                messages,
                options,
                cancellationToken);
            foreach (ChatMessage message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents)
                {
                    ModelId = response.ModelId
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
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
                Config = new AgentConfig { MaxTurns = 3 },
                Model = new LlmConfig { ModelId = "approval-model" }
            });
    }

    private sealed class FakeChatClientFactory(IChatClient provider) : IAgentChatClientFactory
    {
        public IChatClient Create(LlmConfig llm) => provider;
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public string UserId => Requester.UserId;
        public string? TenantId => Requester.TenantId;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private sealed class EmptyFileAssetRepository : IFileAssetRepository
    {
        public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class ApprovalRuntime(
        ServiceProvider serviceProvider,
        AsyncServiceScope scope,
        AgentExecutor executor,
        IHumanApprovalService approvals,
        InMemoryConversationStore conversations,
        HighRiskCapabilitySource capability) : IAsyncDisposable
    {
        internal AgentExecutor Executor { get; } = executor;
        internal IHumanApprovalService Approvals { get; } = approvals;
        internal InMemoryConversationStore Conversations { get; } = conversations;
        internal HighRiskCapabilitySource Capability { get; } = capability;

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }
    }
}
