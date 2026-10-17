using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>P2 deterministic fault injection (V02/V10/V12/V18): every failure path converges inside the deadline.</summary>
internal static class FaultScenario
{
    internal static async Task RunAsync()
    {
        await MalformedPayloadRecoversAsync().ConfigureAwait(false);
        await UnconfirmedCancelClosesAsync().ConfigureAwait(false);
        await QuotaFailsLocallyAsync().ConfigureAwait(false);
        await EofConvergesAsync().ConfigureAwait(false);
        await TransportFaultPreservesTerminalAsync().ConfigureAwait(false);
        await InvalidHandlerResultIsLocalAsync().ConfigureAwait(false);
    }

    private static async Task MalformedPayloadRecoversAsync()
    {
        await using RpcHarness harness = await RpcHarness.StartAsync(null, null, (client, agent) =>
        {
            client.RegisterRequestHandler("session/request_permission", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"outcome\":{\"outcome\":\"cancelled\"}}")));
            agent.RegisterRequestHandler("test/after", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"ok\":true}")));
        }).ConfigureAwait(false);
        // A known method in this direction with an empty payload fails its schema and is answered, not fatal.
        await RpcScenario.SendRawAsync(harness.AgentWire, "{\"id\":1,\"method\":\"session/request_permission\",\"params\":{}}").ConfigureAwait(false);
        using (JsonDocument invalidParams = await RpcScenario.ReceiveAsync(harness.ClientWire).ConfigureAwait(false))
        {
            RpcScenario.AssertEqual(ErrorCode.InvalidParams, invalidParams.RootElement.GetProperty("error").GetProperty("code").GetInt32(), "malformed payload");
        }

        JsonElement ok = await harness.Client.RequestAsync("test/after", AcpJson.ParseElement("{}"), harness.Deadline.Token).ConfigureAwait(false);
        RpcScenario.AssertEqual(true, ok.GetProperty("ok").GetBoolean(), "connection recovers after a rejected request");
    }

    private static async Task UnconfirmedCancelClosesAsync()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcHarness harness = await RpcHarness.StartAsync(
            new AcpRpcOptions { CancelGracePeriod = TimeSpan.FromMilliseconds(300) },
            null,
            (_, agent) =>
            {
                agent.RegisterRequestHandler("test/ignores-cancel", async (_, ct) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation is deliberately ignored.
                    }

                    await release.Task.ConfigureAwait(false);
                    return AcpJson.ParseElement("{}");
                });
            }).ConfigureAwait(false);

        using CancellationTokenSource caller = new();
        Task<JsonElement> call = harness.Client.RequestAsync("test/ignores-cancel", AcpJson.ParseElement("{}"), caller.Token);
        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        await caller.CancelAsync().ConfigureAwait(false);
        await RpcScenario.AssertThrowsAsync<AcpCancellationUnconfirmedException>(() => call).ConfigureAwait(false);
        await harness.Client.Completion.ConfigureAwait(false);
        release.TrySetResult();
    }

    private static async Task QuotaFailsLocallyAsync()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcHarness harness = await RpcHarness.StartAsync(
            new AcpRpcOptions { MaxPendingRequests = 1 },
            null,
            (_, agent) => agent.RegisterRequestHandler("test/hang", async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return AcpJson.ParseElement("{}");
            })).ConfigureAwait(false);

        Task pending = harness.Client.RequestAsync("test/hang", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        await RpcScenario.AssertThrowsAsync<AcpQuotaExceededException>(
            () => harness.Client.RequestAsync("test/hang", AcpJson.ParseElement("{}"), harness.Deadline.Token)).ConfigureAwait(false);
        // Quota exhaustion never blocks the response path.
        release.TrySetResult();
        await pending.ConfigureAwait(false);
    }

    private static async Task EofConvergesAsync()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcHarness harness = await RpcHarness.StartAsync(null, null, (_, agent) =>
            agent.RegisterRequestHandler("test/hang", async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return AcpJson.ParseElement("{}");
            })).ConfigureAwait(false);

        Task<JsonElement> pending = harness.Client.RequestAsync("test/hang", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        harness.ClientWire.SimulateEof();
        await RpcScenario.AssertThrowsAsync<AcpConnectionClosedException>(() => pending).ConfigureAwait(false);
        await harness.Client.Completion.ConfigureAwait(false);
        release.TrySetResult();
    }

    private static async Task TransportFaultPreservesTerminalAsync()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcHarness harness = await RpcHarness.StartAsync(null, null, (_, agent) =>
            agent.RegisterRequestHandler("test/reply", async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return AcpJson.ParseElement("{}");
            })).ConfigureAwait(false);

        Task<JsonElement> pending = harness.Client.RequestAsync("test/reply", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        // A real transport reports this for an oversized frame; the engine must converge without hanging.
        harness.ClientWire.Fault(new InvalidDataException("ACP message exceeds MaxMessageBytes."));
        release.TrySetResult();
        await RpcScenario.AssertThrowsAsync<AcpConnectionClosedException>(() => pending).ConfigureAwait(false);
        await RpcScenario.AssertThrowsAsync<Exception>(() => harness.Client.Completion).ConfigureAwait(false);
    }

    private static async Task InvalidHandlerResultIsLocalAsync()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RpcHarness harness = await RpcHarness.StartAsync(null, null, (_, agent) =>
        {
            // The agent answers an initialize request with a result that violates the method schema.
            agent.RegisterRequestHandler("initialize", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"unexpected\":true}")));
            agent.RegisterRequestHandler("test/after", async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return AcpJson.ParseElement("{\"ok\":true}");
            });
        }).ConfigureAwait(false);

        await RpcScenario.AssertThrowsAsync<JsonException>(() => harness.Client.RequestAsync(
            "initialize", AcpJson.ParseElement("{\"protocolVersion\":1}"), harness.Deadline.Token)).ConfigureAwait(false);
        // The link stays usable: the next roundtrip answers normally.
        release.TrySetResult();
        JsonElement ok = await harness.Client.RequestAsync("test/after", AcpJson.ParseElement("{}"), harness.Deadline.Token).ConfigureAwait(false);
        RpcScenario.AssertEqual(true, ok.GetProperty("ok").GetBoolean(), "connection survives a schema-invalid result");
    }
}
