using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>P2 engine basics: request/response, IDs, errors, batch, ordered notifications, and diagnostics.</summary>
internal static class RpcScenario
{
    internal static async Task RunAsync()
    {
        List<AcpRpcDiagnosticEvent> diagnostics = [];
        List<string> notes = [];
        List<int> order = [];
        await using RpcHarness harness = await RpcHarness.StartAsync(
            new AcpRpcOptions { Diagnostic = diagnostics.Add },
            null,
            (client, agent) =>
            {
                agent.RegisterRequestHandler("test/echo", (request, _) => Task.FromResult<JsonElement?>(request.Parameters!.Value));
                agent.RegisterRequestHandler("test/fail", (_, _) => throw new AcpRpcException(new Error(-32000, "auth required", AcpJson.ParseElement("{\"scope\":\"session\"}"))));
                agent.RegisterRequestHandler("test/who", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"side\":\"agent-served\"}")));
                client.RegisterRequestHandler("test/echo", (request, _) => Task.FromResult<JsonElement?>(request.Parameters!.Value));
                client.RegisterRequestHandler("test/who", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"side\":\"client-served\"}")));
                client.RegisterRequestHandler("test/one", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"first\":true}")));
                client.RegisterRequestHandler("test/two", (_, _) => Task.FromResult<JsonElement?>(AcpJson.ParseElement("{\"second\":true}")));
                // Cancel inside a batch dispatches immediately instead of queueing behind other work.
                client.RegisterRequestHandler("test/slow", async (_, ct) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    return AcpJson.ParseElement("{}");
                });
                client.RegisterNotificationHandler("test/note", (notification, _) =>
                {
                    lock (notes) notes.Add(notification.Method);
                    return Task.CompletedTask;
                });
                client.RegisterNotificationHandler("test/seq", (notification, _) =>
                {
                    lock (order) order.Add(notification.Parameters!.Value.GetProperty("n").GetInt32());
                    return Task.CompletedTask;
                });
            }).ConfigureAwait(false);

        await RoundTripAsync(harness).ConfigureAwait(false);
        await DrainAsync(harness.ClientWire, harness.AgentWire).ConfigureAwait(false);
        await IdKindsAsync(harness).ConfigureAwait(false);
        await DrainAsync(harness.ClientWire, harness.AgentWire).ConfigureAwait(false);
        await SameIdBothDirectionsAsync(harness).ConfigureAwait(false);
        await DrainAsync(harness.ClientWire, harness.AgentWire).ConfigureAwait(false);
        await ErrorsAndDiagnosticsAsync(harness, diagnostics).ConfigureAwait(false);
        await DrainAsync(harness.ClientWire, harness.AgentWire).ConfigureAwait(false);
        await BatchAsync(harness, notes).ConfigureAwait(false);
        await DrainAsync(harness.ClientWire, harness.AgentWire).ConfigureAwait(false);
        await OrderedNotificationsAsync(harness, order).ConfigureAwait(false);
    }

    private static async Task RoundTripAsync(RpcHarness harness)
    {
        JsonElement result = await harness.Client.RequestAsync("test/echo", AcpJson.ParseElement("{\"value\":\"你好🥐\"}"), harness.Deadline.Token).ConfigureAwait(false);
        RpcScenario.AssertEqual("你好🥐", result.GetProperty("value").GetString(), "ext roundtrip");
    }

    private static async Task IdKindsAsync(RpcHarness harness)
    {
        foreach (string id in new[] { "\"str-1\"", "9007199254740993" })
        {
            await SendRawAsync(harness.AgentWire, $"{{\"id\":{id},\"method\":\"test/echo\",\"params\":{{}}}}").ConfigureAwait(false);
            using JsonDocument reply = await ReceiveAsync(harness.ClientWire).ConfigureAwait(false);
            AssertEqual(id.Trim('"'), reply.RootElement.GetProperty("id").GetRawText().Trim('"'), "id echo");
        }

        await SendRawAsync(harness.AgentWire, "{\"id\":null,\"method\":\"test/echo\"}").ConfigureAwait(false);
        using (JsonDocument invalid = await ReceiveAsync(harness.ClientWire).ConfigureAwait(false))
        {
            AssertEqual(ErrorCode.InvalidRequest, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32(), "id:null rejected");
        }

        await SendRawAsync(harness.AgentWire, "not-json").ConfigureAwait(false);
        using (JsonDocument parse = await ReceiveAsync(harness.ClientWire).ConfigureAwait(false))
        {
            AssertEqual(ErrorCode.ParseError, parse.RootElement.GetProperty("error").GetProperty("code").GetInt32(), "parse error");
        }
    }

    private static async Task SameIdBothDirectionsAsync(RpcHarness harness)
    {
        // Outbound tables are per-connection: identical ID values in both directions never collide.
        Task<JsonElement> clientCall = harness.Client.RequestAsync("test/who", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        Task<JsonElement> agentCall = harness.Agent.RequestAsync("test/who", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        AssertEqual("agent-served", (await clientCall.ConfigureAwait(false)).GetProperty("side").GetString(), "client call routed");
        AssertEqual("client-served", (await agentCall.ConfigureAwait(false)).GetProperty("side").GetString(), "agent call routed");
    }

    private static async Task ErrorsAndDiagnosticsAsync(RpcHarness harness, List<AcpRpcDiagnosticEvent> diagnostics)
    {
        AcpRpcException failure = await AssertThrowsAsync<AcpRpcException>(
            () => harness.Client.RequestAsync("test/fail", AcpJson.ParseElement("{}"), harness.Deadline.Token)).ConfigureAwait(false);
        AssertEqual(-32000, failure.Code, "error code preserved");
        AssertEqual("session", failure.Error.Data.Value.GetProperty("scope").GetString(), "error data preserved");

        AcpRpcException missing = await AssertThrowsAsync<AcpRpcException>(
            () => harness.Client.RequestAsync("test/missing", AcpJson.ParseElement("{}"), harness.Deadline.Token)).ConfigureAwait(false);
        AssertEqual(ErrorCode.MethodNotFound, missing.Code, "unregistered extension method");

        // The agent answers twice and once for an ID nobody issued; the engine completes once and diagnoses the rest.
        int before = diagnostics.Count;
        Task<JsonElement> call = harness.Client.RequestAsync("test/echo", AcpJson.ParseElement("{}"), harness.Deadline.Token);
        // The agent's reply lands on the agent's outbound tap; the client tap only carries what the client wrote.
        using JsonDocument reply = await ReceiveResponseAsync(harness.AgentWire).ConfigureAwait(false);
        await SendRawAsync(harness.AgentWire, reply.RootElement.GetRawText()).ConfigureAwait(false);
        await SendRawAsync(harness.AgentWire, reply.RootElement.GetRawText()).ConfigureAwait(false);
        await SendRawAsync(harness.AgentWire, "{\"id\":\"never-issued\",\"result\":{}}").ConfigureAwait(false);
        await call.ConfigureAwait(false);
        await WaitForAsync(() => diagnostics.Skip(before).Any(item => item.Kind == AcpRpcDiagnosticKind.DuplicateResponse), harness.Deadline.Token, "duplicate diagnostic").ConfigureAwait(false);
        await WaitForAsync(() => diagnostics.Skip(before).Any(item => item.Kind == AcpRpcDiagnosticKind.UnmatchedResponse), harness.Deadline.Token, "unmatched diagnostic").ConfigureAwait(false);
    }

    private static async Task BatchAsync(RpcHarness harness, List<string> notes)
    {
        await SendRawAsync(harness.AgentWire, """
            [
              {"id":"a","method":"test/one","params":{}},
              {"method":"test/note","params":{}},
              {"id":"b","method":"test/two","params":{}}
            ]
            """).ConfigureAwait(false);
        using (JsonDocument batch = await ReceiveAsync(harness.ClientWire).ConfigureAwait(false))
        {
            AssertEqual(JsonValueKind.Array, batch.RootElement.ValueKind, "batch reply is an array");
            AssertEqual(2, batch.RootElement.GetArrayLength(), "batch replies per request");
            AssertEqual("a", batch.RootElement[0].GetProperty("id").GetString(), "batch order first");
            AssertEqual("b", batch.RootElement[1].GetProperty("id").GetString(), "batch order second");
        }

        await SendRawAsync(harness.AgentWire, "[{\"method\":\"test/note\",\"params\":{}}]").ConfigureAwait(false);
        await WaitForAsync(() =>
        {
            lock (notes) return notes.Count == 1;
        }, harness.Deadline.Token, "batch notification").ConfigureAwait(false);

        await SendRawAsync(harness.AgentWire, "[]").ConfigureAwait(false);
        using (JsonDocument empty = await ReceiveAsync(harness.ClientWire).ConfigureAwait(false))
        {
            AssertEqual(ErrorCode.InvalidRequest, empty.RootElement.GetProperty("error").GetProperty("code").GetInt32(), "empty batch");
        }

        await SendRawAsync(harness.AgentWire, """
            [
              {"id":"slow","method":"test/slow","params":{}},
              {"method":"$/cancel_request","params":{"requestId":"slow"}}
            ]
            """).ConfigureAwait(false);
        using (JsonDocument cancelled = await ReceiveAsync(harness.ClientWire).ConfigureAwait(false))
        {
            AssertEqual(ErrorCode.RequestCancelled, cancelled.RootElement[0].GetProperty("error").GetProperty("code").GetInt32(), "in-batch cancel");
        }
    }

    private static async Task OrderedNotificationsAsync(RpcHarness harness, List<int> order)
    {
        StringBuilder batch = new("[");
        for (int index = 0; index < 5; index++)
        {
            if (index > 0) batch.Append(',');
            batch.Append($"{{\"method\":\"test/seq\",\"params\":{{\"n\":{index}}}}}");
        }

        batch.Append(']');
        await SendRawAsync(harness.AgentWire, batch.ToString()).ConfigureAwait(false);
        await WaitForAsync(() =>
        {
            lock (order) return order.Count == 5;
        }, harness.Deadline.Token, "notification count").ConfigureAwait(false);
        AssertEqual("0,1,2,3,4", string.Join(',', order), "notification order");
    }

    /// <summary>Emits accumulated tap frames between phases so assertions start from a clean slate.</summary>
    internal static async Task DrainAsync(params RpcLoopback[] wires)
    {
        foreach (RpcLoopback wire in wires)
            while (wire.OutgoingFrames.TryRead(out _)) { }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal static async Task SendRawAsync(RpcLoopback wire, string json) =>
        await wire.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);

    /// <summary>Reads frames until one is a response (requests the engine wrote carry a method member).</summary>
    internal static async Task<JsonDocument> ReceiveResponseAsync(RpcLoopback wire)
    {
        while (true)
        {
            JsonDocument frame = await ReceiveAsync(wire).ConfigureAwait(false);
            if (!frame.RootElement.TryGetProperty("method", out _)) return frame;
        }
    }

    internal static async Task<JsonDocument> ReceiveAsync(RpcLoopback wire)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        byte[]? frame = await wire.OutgoingFrames.ReadAsync(deadline.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Expected a wire frame before the deadline.");
        return JsonDocument.Parse(frame);
    }

    internal static void AssertEqual<T>(T expected, T? actual, string what)
    {
        if (!Equals(expected, actual)) throw new InvalidDataException($"{what}: expected {expected}, got {actual}.");
    }

    internal static async Task WaitForAsync(Func<bool> condition, CancellationToken deadline, string what)
    {
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new InvalidDataException($"{what} was not observed within the deadline.");
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static async Task<T> AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception unexpected)
        {
            throw new InvalidDataException($"Expected {typeof(T).Name}, got {unexpected.GetType().Name}: {unexpected.Message}");
        }

        throw new InvalidDataException($"Expected {typeof(T).Name}, but nothing was thrown.");
    }
}
