using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.TestConsole.Agents;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>
/// P3 minimal loop over one in-process stdio-shaped pair: initialize, session/new, streaming
/// echo of Chinese and emoji, per-grapheme update order, and session reuse after a turn.
/// </summary>
internal static class EchoScenario
{
    internal static async Task RunAsync()
    {
        (RpcLoopback clientWire, RpcLoopback agentWire) = RpcLoopback.CreatePair();
        await using AcpAgentConnection agent = new(agentWire, EchoAgent.CreateBuilder(TimeSpan.Zero));
        await using AcpClientConnection client = new(clientWire);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        agent.Start();
        client.Start();
        Task consumer = Task.CompletedTask;
        try
        {
            InitializeResponse negotiated = await client.InitializeAsync(deadline.Token).ConfigureAwait(false);
            Assert(negotiated.ProtocolVersion == AcpProtocol.Version, "initialize negotiated v1");

            AcpSession session = await client.NewSessionAsync(Path.GetTempPath(), deadline.Token).ConfigureAwait(false);
            Assert(session.SessionId.Length > 0, "session id allocated");

            List<string> chunks = [];
            consumer = CollectUpdatesAsync(session, chunks, deadline.Token);

            string first = "Echo: 你好 ACP 🥐";
            PromptResponse final = await PromptAsync(session, "你好 ACP 🥐", chunks, first, deadline.Token).ConfigureAwait(false);
            Assert(final.StopReason == StopReason.EndTurn, "first turn ends with end_turn");
            Assert(chunks.Count >= first.Length / 2, "echo streamed as multiple grapheme updates");

            // The same session accepts the next turn without re-creation (session reuse).
            string second = "Echo: 第二轮 ✔";
            PromptResponse reuse = await PromptAsync(session, "第二轮 ✔", chunks, second, deadline.Token).ConfigureAwait(false);
            Assert(reuse.StopReason == StopReason.EndTurn, "reused session turn ends with end_turn");
        }
        finally
        {
            // Disposing the client closes the session streams; only then can the consumer finish.
            await client.DisposeAsync().ConfigureAwait(false);
            await agent.DisposeAsync().ConfigureAwait(false);
            await consumer.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task<PromptResponse> PromptAsync(AcpSession session, string prompt, List<string> chunks, string expectedEcho, CancellationToken deadline)
    {
        int before = chunks.Count;
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText(prompt))], deadline).ConfigureAwait(false);
        PromptResponse final = await turn.Completion.WaitAsync(deadline).ConfigureAwait(false);
        // Updates enqueued before Completion are visible to the consumer without further waits.
        await WaitForAsync(() => Joined(chunks, before) == expectedEcho, deadline, "echo text");
        return final;
    }

    internal static async Task CollectUpdatesAsync(AcpSession session, List<string> chunks, CancellationToken deadline)
    {
        try
        {
            await foreach (SessionNotification update in session.ReadUpdatesAsync(deadline).ConfigureAwait(false))
            {
                if (update.Update.Json.TryGetProperty("sessionUpdate", out var tag) && tag.GetString() == "agent_message_chunk")
                {
                    lock (chunks) chunks.Add(update.Update.AsAgentMessageChunk().Content.AsText().Text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Scenario teardown cancels the stream; collected chunks stay readable for asserts.
        }
    }

    internal static async Task WaitForAsync(Func<bool> condition, CancellationToken deadline, string what)
    {
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new InvalidDataException($"{what} was not observed within the deadline.");
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static int Count(List<string> chunks)
    {
        lock (chunks) return chunks.Count;
    }

    internal static string Joined(List<string> chunks, int before)
    {
        lock (chunks) return string.Concat(chunks.Skip(before));
    }

    internal static void Assert(bool condition, string what)
    {
        if (!condition) throw new InvalidDataException($"{what} failed.");
    }
}
