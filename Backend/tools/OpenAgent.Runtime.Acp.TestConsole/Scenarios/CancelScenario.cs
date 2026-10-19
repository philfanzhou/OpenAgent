using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.TestConsole.Agents;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>
/// P3 cancellation over one in-process pair: the client cancels after the first streamed
/// chunk, the echo agent answers with a trailing update and the final cancelled response, and
/// the session keeps accepting turns afterwards. The client never treats "cancel sent" as
/// "cancel confirmed" — the final response decides.
/// </summary>
internal static class CancelScenario
{
    internal static async Task RunAsync()
    {
        (RpcLoopback clientWire, RpcLoopback agentWire) = RpcLoopback.CreatePair();
        await using AcpAgentConnection agent = new(agentWire, EchoAgent.CreateBuilder(Timeout.InfiniteTimeSpan));
        await using AcpClientConnection client = new(clientWire);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        agent.Start();
        client.Start();
        Task consumer = Task.CompletedTask;
        try
        {
            await client.InitializeAsync(deadline.Token).ConfigureAwait(false);
            AcpSession session = await client.NewSessionAsync(Path.GetTempPath(), deadline.Token).ConfigureAwait(false);

            List<string> chunks = [];
            consumer = EchoScenario.CollectUpdatesAsync(session, chunks, deadline.Token);

            AcpPromptTurn turn = await session.StartPromptAsync(
                [new ContentBlock(new ContentBlockText("这是一段用于取消测试的中文文本 ✂️"))],
                deadline.Token).ConfigureAwait(false);
            await EchoScenario.WaitForAsync(() => chunks.Count > 0, deadline.Token, "first chunk").ConfigureAwait(false);
            await turn.CancelAsync(deadline.Token).ConfigureAwait(false);

            PromptResponse final = await turn.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            EchoScenario.Assert(final.StopReason == StopReason.Cancelled, "cancel confirmed by the final response");
            // The trailing update stays ordered ahead of completion (wire order).
            await EchoScenario.WaitForAsync(
                () => Text(chunks).EndsWith(EchoAgent.CancelTail, StringComparison.Ordinal),
                deadline.Token,
                "cancel-tail update").ConfigureAwait(false);
            // The agent pauses after the first grapheme, so the partial echo holds exactly one chunk plus the tail.
            EchoScenario.Assert(Text(chunks).Length > EchoAgent.CancelTail.Length, "partial echo preserved");

            // A cancelled session returns to ready and keeps serving turns.
            int before = EchoScenario.Count(chunks);
            AcpPromptTurn next = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("继续对话"))], deadline.Token).ConfigureAwait(false);
            PromptResponse after = await next.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            EchoScenario.Assert(after.StopReason == StopReason.EndTurn, "session reusable after cancel");
            await EchoScenario.WaitForAsync(
                () => EchoScenario.Joined(chunks, before) == "Echo: 继续对话",
                deadline.Token,
                "post-cancel echo").ConfigureAwait(false);
        }
        finally
        {
            // Disposing the client closes the session streams; only then can the consumer finish.
            await client.DisposeAsync().ConfigureAwait(false);
            await agent.DisposeAsync().ConfigureAwait(false);
            await consumer.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static string Text(List<string> chunks)
    {
        lock (chunks) return string.Concat(chunks);
    }

    private static string Tail(List<string> chunks)
    {
        lock (chunks) return chunks.Count == 0 ? string.Empty : chunks[^1];
    }
}
