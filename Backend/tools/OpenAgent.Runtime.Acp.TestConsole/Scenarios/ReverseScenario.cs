using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>P2 reentrancy (V05): a request in flight never blocks the reverse direction, responses, or cancels.</summary>
internal static class ReverseScenario
{
    internal static async Task RunAsync()
    {
        TaskCompletionSource slowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> notes = [];
        await using (RpcHarness harness = await RpcHarness.StartAsync(
            null,
            null,
            (client, agent) =>
            {
                agent.RegisterRequestHandler("test/outer", async (_, ct) =>
                {
                    // The A→B request waits inside its handler for a B→A reverse call that must complete.
                    JsonElement inner = await agent.RequestAsync("test/inner", AcpJson.ParseElement("{}"), ct).ConfigureAwait(false);
                    return AcpJson.ParseElement($"{{\"seen\":\"{inner.GetProperty("ok").GetString()}\"}}");
                });
                client.RegisterRequestHandler("test/inner", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"ok\":\"inner\"}")));
                agent.RegisterRequestHandler("test/hang", async (_, _) =>
                {
                    await slowRelease.Task.ConfigureAwait(false);
                    return AcpJson.ParseElement("{}");
                });
                agent.RegisterRequestHandler("test/fast", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"speed\":\"fast\"}")));
                agent.RegisterRequestHandler("test/quick", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"done\":true}")));
                client.RegisterNotificationHandler("test/during", (notification, _) =>
                {
                    lock (notes) notes.Add(notification.Method);
                    return Task.CompletedTask;
                });
            }).ConfigureAwait(false))
        {
            await ReverseCallInsideRequestAsync(harness).ConfigureAwait(false);
            await SlowHandlerKeepsLinkAliveAsync(harness, slowRelease, notes).ConfigureAwait(false);
            await NaturalCompletionWinsCancelRaceAsync(harness).ConfigureAwait(false);
        }

        await PeerCancelStillExpectsFinalResponseAsync().ConfigureAwait(false);
    }

    private static async Task ReverseCallInsideRequestAsync(RpcHarness harness)
    {
        JsonElement result = await harness.Client.RequestAsync("test/outer", AcpJson.ParseElement("{}"), harness.Deadline.Token).ConfigureAwait(false);
        RpcScenario.AssertEqual("inner", result.GetProperty("seen").GetString(), "reentrant reverse call");
    }

    private static async Task SlowHandlerKeepsLinkAliveAsync(RpcHarness harness, TaskCompletionSource slowRelease, List<string> notes)
    {
        Task<JsonElement> hung = harness.Client.RequestAsync("test/hang", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        JsonElement fast = await harness.Client.RequestAsync("test/fast", AcpJson.ParseElement("{}"), harness.Deadline.Token).ConfigureAwait(false);
        RpcScenario.AssertEqual("fast", fast.GetProperty("speed").GetString(), "call during slow handler");

        await RpcScenario.SendRawAsync(harness.AgentWire, "{\"method\":\"test/during\",\"params\":{}}").ConfigureAwait(false);
        await RpcScenario.WaitForAsync(() =>
        {
            lock (notes) return notes.Count == 1;
        }, harness.Deadline.Token, "notification during slow handler").ConfigureAwait(false);
        slowRelease.TrySetResult();
        await hung.ConfigureAwait(false);
    }

    private static async Task NaturalCompletionWinsCancelRaceAsync(RpcHarness harness)
    {
        using CancellationTokenSource caller = new();
        Task<JsonElement> call = harness.Client.RequestAsync("test/quick", AcpJson.ParseElement("{}"), caller.Token);
        JsonElement answer = await call.ConfigureAwait(false);
        RpcScenario.AssertEqual(true, answer.GetProperty("done").GetBoolean(), "natural completion");
        await caller.CancelAsync().ConfigureAwait(false);
        if (!call.IsCompletedSuccessfully) throw new InvalidDataException("Late cancel disturbed a finished call.");
    }

    private static async Task PeerCancelStillExpectsFinalResponseAsync()
    {
        // A fresh harness keeps the first engine-generated ID deterministic (acp-1).
        TaskCompletionSource handlerRunning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcHarness harness = await RpcHarness.StartAsync(null, null, (_, agent) =>
        {
            agent.RegisterRequestHandler("test/cancel-then-answer", async (_, ct) =>
            {
                handlerRunning.TrySetResult();
                await Task.Delay(80, ct).ConfigureAwait(false);
                return AcpJson.ParseElement("{\"answered\":true}");
            });
        }).ConfigureAwait(false);

        Task<JsonElement> call = harness.Client.RequestAsync("test/cancel-then-answer", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        await handlerRunning.Task.ConfigureAwait(false);
        // The peer cancels our outbound request but still owes a final answer; the call resolves with it.
        await RpcScenario.SendRawAsync(harness.AgentWire, "{\"method\":\"$/cancel_request\",\"params\":{\"requestId\":\"acp-1\"}}").ConfigureAwait(false);
        JsonElement answer = await call.ConfigureAwait(false);
        RpcScenario.AssertEqual(true, answer.GetProperty("answered").GetBoolean(), "final answer after peer cancel");
    }
}
