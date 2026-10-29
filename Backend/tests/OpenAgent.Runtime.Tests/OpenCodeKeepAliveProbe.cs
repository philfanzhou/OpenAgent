using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>Opt-in bounded benchmark. The Python supervisor samples only this test's process tree.</summary>
[Collection(RealOpenCodeCollection.Name)]
public sealed class OpenCodeKeepAliveProbe
{
    [KeepAliveProbeFact]
    public async Task Compare_BoundedTurnsAndIdle_EmitsMeasurements()
    {
        string output = Environment.GetEnvironmentVariable("OPENAGENT_KEEPALIVE_PROBE")!;
        int seconds = int.Parse(Environment.GetEnvironmentVariable("N04_KEEP_SECONDS")!);
        int owners = int.Parse(Environment.GetEnvironmentVariable("N04_OWNERS")!);
        Assert.InRange(owners, 1, 2);
        Assert.InRange(seconds, 0, 30);
        int requests = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(
            (context, _) => ModelFixture.TextAsync(context, "fixture response"),
            () => Interlocked.Increment(ref requests));
        await using RuntimeFixture runtime = new(model.Endpoint, settings =>
        {
            settings["OpenCode:KeepAliveSeconds"] = seconds.ToString();
            settings["OpenCode:MaxKeptProcesses"] = "2";
            settings["OpenCode:MaxConcurrentTurns"] = "2";
        });
        List<object> measurements = [];
        string?[] checkpoints = new string?[owners];
        async Task TurnAsync(int owner, int turn)
        {
            RuntimeInvocation request = runtime.Request with
            {
                Turn = new TurnContext { TenantId = "probe", UserId = "probe", ConversationId = "owner-" + owner, TraceId = "probe" },
                Messages = [new("user", turn == 0 ? "hello" : "follow up")],
                HistoryCheckpoint = checkpoints[owner] ?? "empty",
                CommitAsync = _ => Task.FromResult<string?>(checkpoints[owner] = "committed-" + turn)
            };
            Stopwatch clock = Stopwatch.StartNew();
            long? ttft = null;
            string? version = null;
            System.Text.StringBuilder content = new();
            await foreach (AgentStreamEvent item in runtime.Driver.RunAsync(request))
            {
                if (item.Type == AgentStreamEventType.Content)
                {
                    ttft ??= clock.ElapsedMilliseconds;
                    content.Append(item.Content);
                }
                version ??= item.Runtime?.AgentVersion;
            }
            Assert.Equal("fixture response", content.ToString());
            lock (measurements)
            {
                measurements.Add(new { Owner = owner, Turn = turn, TtftMs = ttft, TotalMs = clock.ElapsedMilliseconds, Version = version });
            }
        }
        for (int turn = 0; turn < 4; turn++)
        {
            await Task.WhenAll(Enumerable.Range(0, owners).Select(owner => TurnAsync(owner, turn)));
        }
        // A structured phase file aligns supervisor CPU samples with the idle window.
        await Task.Delay(2000);
        int beforeIdle = Volatile.Read(ref requests);
        await File.WriteAllTextAsync(output + ".phase", "idle");
        await Task.Delay(8000);
        int idleRequests = Volatile.Read(ref requests) - beforeIdle;
        await File.WriteAllTextAsync(output + ".phase", "closing");
        var snapshot = runtime.Keeper.Snapshot();
        await runtime.Keeper.DisposeAsync();
        runtime.AssertWorkspacesReleased();
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            KeepAliveSeconds = seconds, Owners = owners, Turns = measurements,
            snapshot.Hits, snapshot.Misses, snapshot.Kept, snapshot.Retired, snapshot.AgesSeconds,
            IdleModelRequests = idleRequests, ModelRequests = requests,
            CleanupVerified = true
        }));
    }
}

internal sealed class KeepAliveProbeFactAttribute : OpenCodeFactAttribute
{
    public KeepAliveProbeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAGENT_KEEPALIVE_PROBE")))
        {
            Skip = "Run scripts/probe-opencode-keepalive.py for the bounded resource benchmark.";
        }
    }
}
