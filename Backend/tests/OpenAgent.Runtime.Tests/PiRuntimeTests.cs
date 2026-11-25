using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Pi;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class PiRuntimeTests
{
    private const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGOoZ/gPAAKAAX8wLww/AAAAAElFTkSuQmCC";

    [PiAdapterFact]
    public async Task Pi_StreamsAnAnswerFromCommittedPlatformHistoryAndCallsRealMcpBridge()
    {
        int calls = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            Assert.Contains("Earlier platform question", request.GetRawText());
            Assert.Contains("Continue the platform conversation", request.GetRawText());
            Assert.Contains("Platform agent instructions", request.GetRawText());
            JsonElement image = Assert.Single(request.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("content").ValueKind == JsonValueKind.Array)
                .SelectMany(message => message.GetProperty("content").EnumerateArray())
                .Where(item => item.GetProperty("type").GetString() == "image_url"));
            Assert.StartsWith("data:image/", image.GetProperty("image_url").GetProperty("url").GetString());
            int results = request.GetProperty("messages").EnumerateArray().Count(item => item.GetProperty("role").GetString() == "tool");
            if (results < 2)
            {
                string name = results == 0 ? "tool_search" : "mcp__openagent__fixture_echo";
                string arguments = results == 0 ? "{\"query\":\"echo isolated fixture marker\"}" : "{}";
                await ModelFixture.ChunkAsync(context, new { role = "assistant", tool_calls = new[] {
                    new { index = 0, id = "fixture-" + results, type = "function", function = new { name, arguments } }
                } }, "tool_calls");
                return;
            }
            Assert.Contains("authorized-bridge-result", request.GetRawText());
            await ModelFixture.TextAsync(context, "Pi answered from history.");
        });
        await using RuntimeToolBridge bridge = new();
        PiRuntime runtime = new(new PiOptions
        {
            Enabled = true, Executable = Environment.GetEnvironmentVariable("OPENAGENT_TEST_PI_ACP")!, AllowedTenants = ["tenant"]
        }, bridge, NullLogger<PiRuntime>.Instance);
        RuntimeInvocation invocation = new(new AgentRuntimeProfile
        {
            AgentId = "default", Config = new AgentConfig(),
            Model = new LlmConfig { ModelId = "fixture", Endpoint = model.Endpoint, ApiKey = "fixture-key", ContextTokens = 16_000, Modality = ModelModality.Multimodal },
            Runtime = new RuntimeProfile { Id = "openagent-pi", Provider = "Pi", TurnTimeoutSeconds = 30 }
        }, new TurnContext { TenantId = "tenant", UserId = "user", ConversationId = "same-conversation", TraceId = "fixture-trace" },
            "Platform agent instructions", [new("user", "Earlier platform question"), new("assistant", "Earlier answer"),
                new("user", "Continue the platform conversation", [new("image/png", Convert.FromBase64String(PixelPng))])],
            [new RuntimeTool("fixture_echo", "Echo isolated fixture marker", "{\"type\":\"object\"}", (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("authorized-bridge-result");
            })]);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent item in runtime.RunAsync(invocation))
            events.Add(item);

        Assert.Equal("Pi", Assert.Single(events, item => item.Type == AgentStreamEventType.RuntimeStarted).Runtime!.Provider);
        Assert.Equal("Pi answered from history.", string.Concat(events.Where(item => item.Type == AgentStreamEventType.Content).Select(item => item.Content)));
        Assert.Equal(1, calls);
    }

    [PiAdapterFact]
    public async Task Pi_CallerCancellationStopsTheModelRequest()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, _) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            finally { stopped.TrySetResult(); }
        });
        await using RuntimeToolBridge bridge = new();
        PiRuntime runtime = new(new PiOptions
        {
            Enabled = true, Executable = Environment.GetEnvironmentVariable("OPENAGENT_TEST_PI_ACP")!, AllowedTenants = ["tenant"]
        }, bridge, NullLogger<PiRuntime>.Instance);
        RuntimeInvocation invocation = new(new AgentRuntimeProfile
        {
            AgentId = "default", Config = new AgentConfig(),
            Model = new LlmConfig { ModelId = "fixture", Endpoint = model.Endpoint, ApiKey = "fixture-key" },
            Runtime = new RuntimeProfile { Id = "openagent-pi", Provider = "Pi", TurnTimeoutSeconds = 30 }
        }, new TurnContext { TenantId = "tenant", UserId = "user", ConversationId = "cancel-conversation", TraceId = "fixture-cancel" },
            "Platform agent instructions", [new("user", "Start a cancellable request")], []);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        Task reading = ReadUntilCompletionAsync();
        await started.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        async Task ReadUntilCompletionAsync()
        {
            await foreach (AgentStreamEvent _ in runtime.RunAsync(invocation, cancellation.Token)) { }
        }
    }

    private sealed class PiAdapterFactAttribute : FactAttribute
    {
        public PiAdapterFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAGENT_TEST_PI_ACP")))
                Skip = "Requires the pinned openma-pi-acp adapter.";
        }
    }
}
