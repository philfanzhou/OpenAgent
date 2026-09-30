using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public sealed class TurnCompactionStrategyTests
{
    [Fact]
    public async Task CompactAsync_OversizedUnicodeTranscript_ChunksWithinSummaryWindow()
    {
        var provider = new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "confirmed continuation state")));
        var bounded = new OutputTokenLimitedChatClient(provider, 128, 819);
        var strategy = new TurnCompactionStrategy(bounded, 1, "compress quoted context", 0, 4_096);
        List<ChatMessage> messages = [new(ChatRole.User, new string('摘', 10_000)),
            new(ChatRole.Assistant, "old reply"), new(ChatRole.User, "active query")];

        await CompactionProvider.CompactAsync(strategy, messages);

        Assert.True(provider.Requests.Count > 1);
        Assert.All(provider.Requests, request =>
        {
            string wire = System.Text.Json.JsonSerializer.Serialize(request.Select(message => new
                { role = message.Role.ToString(), content = message.Text }),
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            int inputTokens = (int)Math.Ceiling(System.Text.Encoding.UTF8.GetByteCount(wire) / 4d);
            Assert.InRange(inputTokens + bounded.GenerationTokenLimit, 1, 4_096);
        });
        Assert.Contains("Continuation state", provider.Requests[1].Last().Text);
    }

    [Fact]
    public async Task CompactAsync_RecentTurnExceedsBudget_SummarizesItWhileKeepingActiveUser()
    {
        List<ChatMessage> messages = [new(ChatRole.User, new string('x', 12_000)),
            new(ChatRole.Assistant, "old answer"), new(ChatRole.User, "active task")];
        var strategy = new TurnCompactionStrategy(new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "state"))), 2, "compress", 2_000);

        List<ChatMessage> result = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        Assert.Equal("active task", Assert.Single(result, message => message.Role == ChatRole.User).Text);
        Assert.DoesNotContain(result, message => message.Text.Length >= 12_000);
    }

    [Fact]
    public async Task CompactAsync_RecentTurnsContainManyTools_PreservesWholeTurnsAndCurrentQuery()
    {
        List<ChatMessage> messages = [new(ChatRole.System, "persistent rules")];
        for (int turn = 0; turn < 4; turn++)
        {
            messages.Add(new(ChatRole.User, $"request-{turn}"));
            messages.Add(new(ChatRole.Assistant, new string('x', 2_000)));
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"call-{turn}", "read", new Dictionary<string, object?>())]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"call-{turn}", "result")]));
        }
        messages.Add(new(ChatRole.User, "active request"));
        var client = new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "task state")));
        var strategy = new TurnCompactionStrategy(client, 2, "compress", 0);

        List<ChatMessage> compacted = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        Assert.Contains(compacted, message => message.Role == ChatRole.System);
        Assert.Equal(["request-3", "active request"], compacted.Where(message => message.Role == ChatRole.User).Select(message => message.Text));
        Assert.Single(compacted.SelectMany(message => message.Contents.OfType<FunctionCallContent>()));
        Assert.Single(compacted.SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
        Assert.Contains(client.LastMessages!, message => message.Role == ChatRole.User);
        Assert.DoesNotContain(client.LastMessages!, message => message.Contents.OfType<FunctionCallContent>().Any());
    }

    [Fact]
    public async Task CompactAsync_LongSingleTurn_PreservesUserAndLatestAtomicToolPairs()
    {
        List<ChatMessage> messages = [new(ChatRole.User, "active task")];
        for (int index = 0; index < 6; index++)
        {
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"call-{index}", "read", new Dictionary<string, object?>())]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"call-{index}", new string('x', 2_000))]));
        }
        var strategy = new TurnCompactionStrategy(new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "tools state"))), 2, "compress", 0);

        List<ChatMessage> compacted = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        Assert.Equal("active task", Assert.Single(compacted, message => message.Role == ChatRole.User).Text);
        Assert.Equal(["call-4", "call-5"], compacted.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).Select(call => call.CallId));
        Assert.Equal(["call-4", "call-5"], compacted.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).Select(call => call.CallId));
    }

    [Fact]
    public async Task CompactAsync_NoOlderCompletedTurn_DoesNotCallSummaryModel()
    {
        var client = new FakeChatProvider(new InvalidOperationException("must not be called"));
        var strategy = new TurnCompactionStrategy(client, 2, "compress", 0);
        List<ChatMessage> messages = [new(ChatRole.User, "hello"), new(ChatRole.Assistant, "reply")];

        Assert.Equal(messages, await CompactionProvider.CompactAsync(strategy, messages));
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task StampNewMessages_ParallelToolsAndPriorSummary_TracksRawStorageBoundary()
    {
        ChatMessage summary = AgentMessageAdapter.FromStored(ConversationSessionStore.Message(20, "summary", "old state"))!;
        ChatMessage retained = AgentMessageAdapter.FromStored(ConversationSessionStore.Message(19, "assistant", "old reply"))!;
        List<ChatMessage> messages = [summary, retained, new(ChatRole.User, "new task"),
            new(ChatRole.Assistant, [new FunctionCallContent("a", "read", new Dictionary<string, object?>()),
                new FunctionCallContent("b", "read", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("a", "a result"), new FunctionResultContent("b", "b result")])];
        var probe = new BoundaryProbe();
        await CompactionProvider.CompactAsync(probe, messages);
        Assert.Equal(25, probe.EndSequence);
        await CompactionProvider.CompactAsync(probe, messages);
        Assert.Equal(25, probe.EndSequence);
        messages.Add(new(ChatRole.Assistant, "complete"));
        await CompactionProvider.CompactAsync(probe, messages);
        Assert.Equal(26, probe.EndSequence);
    }

    private sealed class BoundaryProbe() : CompactionStrategy(CompactionTriggers.Always, CompactionTriggers.Always)
    {
        internal int EndSequence { get; private set; }
        protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index,
            Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
        {
            EndSequence = CompactionMessageMetadata.StampNewMessages(index);
            return ValueTask.FromResult(false);
        }
    }
}
