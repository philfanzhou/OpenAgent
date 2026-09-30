using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;
using ChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class AgentCompactionTests
{
    private static readonly AgentUserContext User = new() { TenantId = "tenant-1", UserId = "user-1" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_AutomaticCompaction_PersistsBoundaryAndContinuesNextTurn(bool streaming)
    {
        var provider = new FakeChatProvider(streaming
            ? [new ChatResponseUpdate(ChatRole.Assistant, "completed response")]
            : []);
        IChatClient model = streaming ? provider : new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "completed response")));
        var summary = new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "confirmed older task state")));
        await using AgentExecutorUsageTests.TestRuntime runtime = CreateRuntime(model, summary);
        await SeedAsync(runtime, "automatic");

        await ExecuteAsync(runtime.Executor, Request("automatic"), streaming);

        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", "automatic"))!;
        ContextSummary audit = Assert.Single(record.ContextSummaries);
        Assert.Equal("Succeeded", audit.Status);
        Assert.Equal("Automatic", audit.Trigger);
        Assert.Equal(9, audit.SourceEndSequence);
        Assert.Equal(10, record.Messages.Count);
        Assert.Contains(audit.CompactedMessages, message => message.Role == "user" && message.Content == "active query");
        Assert.Contains(ConversationSessionStore.ResolveModelHistory(record), message => message.Content == "completed response");

        // This uses the persisted projection through a new Agent/session, not the
        // compaction provider's in-memory state from the first invocation.
        await using AsyncServiceScope nextScope = runtime.CreateScope();
        await ExecuteAsync(nextScope.ServiceProvider.GetRequiredService<AgentExecutor>(), Request("automatic", "next query"), streaming);
        record = (await runtime.Store.GetRecordAsync("tenant-1", "automatic"))!;
        Assert.Equal(12, record.Messages.Count);
        Assert.Equal(2, ConversationSessionStore.ResolveModelHistory(record).Count(message => message.Content == "completed response"));
        Assert.DoesNotContain(ConversationSessionStore.ResolveModelHistory(record), message => message.Content.StartsWith("old-0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactAsync_ManualRequest_UsesSameProjectionAndReleasesLockForNextTurn()
    {
        var model = new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "continued after manual")));
        var summary = new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "manual continuation state")));
        await using AgentExecutorUsageTests.TestRuntime runtime = CreateRuntime(model, summary);
        IConversationCompactionService compaction = runtime.Resolve<IConversationCompactionService>();
        await SeedAsync(runtime, "manual");

        ContextSummary result = await compaction.CompactAsync("tenant-1", "manual", "profile", User);
        await runtime.Executor.ExecuteAsync(Request("manual"), User, CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("Manual", result.Trigger);
        Assert.Equal(8, result.SourceEndSequence);
        Assert.Equal(1, result.ProjectionVersion);
        Assert.Equal(1, result.OriginalStartSequence);
        Assert.Equal(4, result.OriginalEndSequence);
        Assert.Equal(4, Assert.Single(result.CompactedMessages, message => message.Role == "summary").Sequence);
        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", "manual"))!;
        Assert.Equal(10, record.Messages.Count);
        Assert.DoesNotContain(model.LastMessages!, message => message.Text.StartsWith("old-0", StringComparison.Ordinal));
        Assert.Contains(model.LastMessages!, message => message.Text.Contains("manual continuation state", StringComparison.Ordinal));
        Assert.Contains(model.LastMessages!, message => message.Role == ChatRole.User && message.Text == "active query");
    }

    [Fact]
    public async Task ExecuteAsync_SummaryFails_RestoresOriginalHistoryAndPersistsFailure()
    {
        var model = new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "still completed")));
        var summary = new FakeChatProvider(new InvalidOperationException("summary provider unavailable"));
        await using AgentExecutorUsageTests.TestRuntime runtime = CreateRuntime(model, summary);
        await SeedAsync(runtime, "failure");

        await runtime.Executor.ExecuteAsync(Request("failure"), User, CancellationToken.None);

        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", "failure"))!;
        ContextSummary audit = Assert.Single(record.ContextSummaries);
        Assert.Equal("Failed", audit.Status);
        Assert.True(audit.OriginalHistoryRestored);
        Assert.Contains("summary provider unavailable", audit.Error);
        Assert.Contains(model.LastMessages!, message => message.Text.StartsWith("old-0", StringComparison.Ordinal));
        Assert.Equal(10, record.Messages.Count);
    }

    private static AgentExecutorUsageTests.TestRuntime CreateRuntime(IChatClient model, IChatClient summary,
        Action<IServiceCollection>? configure = null) => AgentExecutorUsageTests.CreateRuntime(model, configure: services =>
        {
            services.RemoveAll<IAgentRuntimeResolver>();
            services.AddSingleton<IAgentRuntimeResolver>(new Resolver());
            services.RemoveAll<IAgentChatClientFactory>();
            services.AddSingleton<IAgentChatClientFactory>(new Clients(model, summary));
            configure?.Invoke(services);
        });

    private static async Task SeedAsync(AgentExecutorUsageTests.TestRuntime runtime, string id)
    {
        List<ConversationMessage> messages = Enumerable.Range(0, 8).Select(index =>
            ConversationSessionStore.Message(index + 1, index % 2 == 0 ? "user" : "assistant", $"old-{index} " + new string('x', 3_000))).ToList();
        await runtime.Store.CreateAsync(new ConversationRecord
        {
            ConversationId = id, TenantId = "tenant-1", UserId = "user-1", AgentId = "test-agent",
            Type = ConversationType.User, MessageCount = messages.Count, Messages = messages, Version = 1
        });
    }

    private static AgentRequest Request(string id, string query = "active query") => new()
    {
        AgentId = "test-agent", LlmProfileId = "profile", ConversationId = id, Query = query
    };

    private static async Task ExecuteAsync(AgentExecutor executor, AgentRequest request, bool streaming)
    {
        if (!streaming) await executor.ExecuteAsync(request, User, CancellationToken.None);
        else await foreach (AgentStreamEvent _ in executor.ExecuteStreamingAsync(request, User, CancellationToken.None)) { }
    }

    private sealed class Resolver : IAgentRuntimeResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(string agentId, string llmProfileId, IAgentUserContext userContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRuntimeProfile { AgentId = agentId, Config = new AgentConfig(),
                Model = new LlmConfig { ModelId = "test-model", ContextTokens = 4_096 } });
    }

    private sealed class Clients(IChatClient model, IChatClient summary) : IAgentChatClientFactory
    {
        public IChatClient Create(LlmConfig llm, LlmInteractionCapture? capture = null) => model;
        public IChatClient CreateSummarizationClient(LlmConfig llm, ContextPolicy? policy, LlmInteractionCapture? capture = null) => summary;
    }

}
