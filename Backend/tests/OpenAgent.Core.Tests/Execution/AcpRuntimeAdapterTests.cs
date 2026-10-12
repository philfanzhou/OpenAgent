using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation.Lock;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Execution;
using Xunit;

namespace OpenAgent.Core.Tests.Execution;

public sealed class AcpRuntimeAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _store = new(Mock.Of<ICurrentUserContext>());
    private readonly InMemoryConversationLock _locks = new();

    private AcpRuntimeAdapter CreateAdapter(string policy = "deny", int timeout = 10) => new(
        Options.Create(new AcpOptions
        {
            Enabled = true,
            WorkspaceRoot = _root,
            TurnTimeoutSeconds = timeout,
            RequestTimeoutSeconds = 5,
            Harnesses = [new AcpHarness
            {
                Id = "demo", Name = "ACP demo", Command = "python3", PermissionPolicy = policy,
                Arguments = [Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-demo-harness.py")]
            }]
        }), new ConversationSessionStore(_store, Options.Create(new ConversationStoreOptions())), _locks);

    internal static RuntimeInvocation Invocation(string query, string conversation = "conversation", string tenant = "tenant", string user = "user")
    {
        var config = new AgentConfig { Runtime = new RuntimeBinding { Adapter = "acp", Harness = "demo" } };
        return new RuntimeInvocation(new AgentRequest { Query = query, ConversationId = conversation, LlmProfileId = "preview" },
            new AgentRuntimeProfile { AgentId = "agent", Config = config, Model = new LlmConfig { Provider = "preview", ModelId = "preview", ContextTokens = 32000 } },
            new TurnContext { TenantId = tenant, UserId = user, ConversationId = conversation, AgentId = "agent", TraceId = Guid.NewGuid().ToString("N") },
            new AgentUserContext { TenantId = tenant, UserId = user }, []);
    }

    [Fact]
    public async Task ExecuteAsync_NewAndReloadedSession_PersistsHistoryWithoutReplay()
    {
        AcpRuntimeAdapter adapter = CreateAdapter();
        AgentResponse first = await adapter.ExecuteAsync(Invocation("hello"), CancellationToken.None);
        AgentResponse second = await adapter.ExecuteAsync(Invocation("again"), CancellationToken.None);
        Assert.Equal("ACP demo turn 1: hello", first.Content);
        Assert.Equal("ACP demo turn 2: again", second.Content);
        ConversationRecord record = (await _store.GetRecordAsync("tenant", "conversation"))!;
        Assert.Equal(ConversationStatus.Completed, record.Status);
        Assert.Equal(4, record.Messages.Count);
        Assert.DoesNotContain(record.Messages, message => message.Content.Contains("HISTORY REPLAY"));
        Assert.Equal(record.Messages[0].Metadata!.Extensions!["runtime.session"], record.Messages[2].Metadata!.Extensions!["runtime.session"]);
        Assert.Equal(8, second.TokenUsage!.TotalTokens);
    }

    [Theory]
    [InlineData("deny", "reject")]
    [InlineData("allow_once", "once")]
    public async Task ExecuteStreamingAsync_PermissionDuringPrompt_RespondsWithoutDeadlock(string policy, string decision)
    {
        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent item in CreateAdapter(policy).ExecuteStreamingAsync(Invocation("/permission"), CancellationToken.None))
            events.Add(item);
        Assert.Contains(events, item => item.Type == AgentStreamEventType.ToolCall && item.ToolName == "demo");
        Assert.Contains(events, item => item.Type == AgentStreamEventType.ToolResult && item.Content == decision);
        Assert.Contains(events, item => item.Content == $"Permission decision: {decision}");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CancelledTurn_PreservesPartialAndReleasesLock()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (AgentStreamEvent item in CreateAdapter().ExecuteStreamingAsync(Invocation("/slow"), cancellation.Token))
                if (item.Type == AgentStreamEventType.Content)
                    cancellation.Cancel();
        });
        ConversationRecord record = (await _store.GetRecordAsync("tenant", "conversation"))!;
        Assert.Equal(ConversationStatus.Cancelled, record.Status);
        Assert.Contains(record.Messages, item => item.Content == "partial before cancellation");
        await using IConversationLockHandle? handle = await _locks.TryAcquireAsync("tenant", "conversation", TimeSpan.FromSeconds(10));
        Assert.NotNull(handle);
    }

    [Theory]
    [InlineData("/crash")]
    [InlineData("/oversized")]
    public async Task ExecuteAsync_InvalidProtocol_FailsAndSavesFailedStatus(string query)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => CreateAdapter().ExecuteAsync(Invocation(query), CancellationToken.None));
        Assert.Equal(ConversationStatus.Failed, (await _store.GetRecordAsync("tenant", "conversation"))!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentTenantAndOwner_IsolatesSessionAndWorkspace()
    {
        AcpRuntimeAdapter adapter = CreateAdapter();
        await adapter.ExecuteAsync(Invocation("first"), CancellationToken.None);
        AgentResponse other = await adapter.ExecuteAsync(Invocation("other", tenant: "other-tenant", user: "other-user"), CancellationToken.None);
        Assert.Equal("ACP demo turn 1: other", other.Content);
        Assert.Equal(2, Directory.GetDirectories(_root).Length);
        await Assert.ThrowsAsync<AgentException>(() => adapter.ExecuteAsync(Invocation("forbidden", user: "other-user"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_TurnDeadline_FailsAndTerminatesHarness()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateAdapter(timeout: 1).ExecuteAsync(Invocation("/slow"), CancellationToken.None));
        Assert.Equal(ConversationStatus.Failed, (await _store.GetRecordAsync("tenant", "conversation"))!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedPlatformBinding_RejectsBeforeLaunching()
    {
        RuntimeInvocation invocation = Invocation("hello");
        invocation.Profile.Config.Rag.Enabled = true;
        await Assert.ThrowsAsync<AgentException>(() => CreateAdapter().ExecuteAsync(invocation, CancellationToken.None));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ConsumerStops_PersistsCancelledPartialAndEndsHarness()
    {
        await using (IAsyncEnumerator<AgentStreamEvent> events = CreateAdapter().ExecuteStreamingAsync(Invocation("/slow"), CancellationToken.None).GetAsyncEnumerator())
        { Assert.True(await events.MoveNextAsync()); }
        Assert.Equal(ConversationStatus.Cancelled, (await _store.GetRecordAsync("tenant", "conversation"))!.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
