using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Runtime;
using OpenAgent.Runtime.Tests;
using OpenAgent.Engine.Tests.TestDoubles;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>Opt-in, controlled single-tenant baseline; no claim of production isolation/capacity.</summary>
[Collection(RealOpenCodeCollection.Name)]
public sealed class OpenCodeCapacityTests
{
    [CapacityFact]
    public async Task Capacity_ColdSingleTenant_RecordsStepsAndStopsAtResourceLimit()
    {
        string output = Environment.GetEnvironmentVariable("OPENAGENT_CAPACITY_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "openagent-capacity.json");
        string root = Path.Combine(Path.GetTempPath(), "openagent-capacity-" + Guid.NewGuid().ToString("N"));
        List<object> results = [];
        ResourceSample baseline = await SampleAsync();
        try
        {
            await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
            {
                await Task.Delay(100, context.RequestAborted);
                JsonElement[] tools = request.GetProperty("messages").EnumerateArray()
                    .Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
                if (tools.Length > 0)
                {
                    Assert.Contains("N03_NATIVE_OK", tools[^1].GetRawText());
                    await ModelFixture.TextAsync(context, "capacity-ok");
                    return;
                }
                const string command = """
                    set -e
                    node -e 'const fs=require("node:fs"); if(process.getuid()===0) throw Error("root"); fs.writeFileSync("node.json",JSON.stringify({n:21})); if(JSON.parse(fs.readFileSync("node.json")).n!==21) throw Error("data");'
                    python3 -c 'import json,pathlib; p=pathlib.Path("python.json"); p.write_text(json.dumps({"n":21})); assert json.loads(p.read_text())["n"]==21; print("N03_NATIVE_OK")'
                    """;
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant", tool_calls = new[] { new { index = 0, id = "native-capacity", type = "function",
                        function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command }) } } }
                }, "tool_calls");
            });
            foreach (int capacity in new[] { 1, 2, 4, 8 })
            {
                int active = 0, waiting = 0, peakActive = 0, peakWaiting = 0;
                using MeterListener metrics = new();
                metrics.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "OpenAgent.Admission") listener.EnableMeasurementEvents(instrument);
                };
                object gate = new();
                metrics.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                {
                    lock (gate)
                    {
                        if (instrument.Name == "openagent_admission_active") peakActive = Math.Max(peakActive, active += (int)value);
                        if (instrument.Name == "openagent_admission_waiting") peakWaiting = Math.Max(peakWaiting, waiting += (int)value);
                    }
                });
                metrics.Start();
                IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenCode:Executable"] = Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE"),
                    ["OpenCode:WorkspaceRoot"] = root,
                    ["OpenCode:MaxConcurrentTurns"] = capacity.ToString(CultureInfo.InvariantCulture),
                    ["OpenCode:KeepAliveSeconds"] = "0",
                    ["OpenCode:AllowedNativeTools:0"] = "read",
                    ["OpenCode:AllowedNativeTools:1"] = "edit",
                    ["OpenCode:AllowedNativeTools:2"] = "write",
                    ["OpenCode:AllowedNativeTools:3"] = "bash",
                    ["OpenCode:TurnTimeoutSeconds"] = "60"
                }).Build();
                Dictionary<string, string?> settings = new()
                {
                    ["RuntimeAdmission:MaxConcurrentTurns"] = capacity.ToString(CultureInfo.InvariantCulture),
                    ["RuntimeAdmission:MaxConcurrentPerTenant"] = capacity.ToString(CultureInfo.InvariantCulture),
                    ["RuntimeAdmission:MaxQueuedTurns"] = (capacity * 2).ToString(CultureInfo.InvariantCulture),
                    ["RuntimeAdmission:MaxQueuedPerTenant"] = (capacity * 2).ToString(CultureInfo.InvariantCulture),
                    ["RuntimeAdmission:QueueTimeoutSeconds"] = "60"
                };
                await using StreamingHost host = await StreamingHost.StartAsync(settings: settings,
                    configure: services => services.AddOpenCodeRuntime(config),
                    model: new LlmConfig { ModelId = "fixture", Endpoint = model.Endpoint, ApiKey = "local-test", ContextTokens = 100000 });
                using CancellationTokenSource step = new(TimeSpan.FromSeconds(120));
                Task<Observation>[] runs = Enumerable.Range(0, capacity * 2).Select(i => RunAsync(host, $"capacity-{capacity}-{i}", step.Token)).ToArray();
                Task all = Task.WhenAll(runs);
                double cpu = 0;
                long rss = 0;
                int processes = 0, excessive = 0;
                bool stopped = false;
                while (!all.IsCompleted)
                {
                    ResourceSample sample = await SampleAsync();
                    cpu = Math.Max(cpu, sample.CpuPercent);
                    rss = Math.Max(rss, sample.RssKiB);
                    processes = Math.Max(processes, sample.OpenCodeProcesses);
                    // CPU is percent of one core. Stop on 3 consecutive samples > 6 cores,
                    // or immediately above 3 GiB RSS for this testhost and its descendants.
                    excessive = sample.CpuPercent > 600 ? excessive + 1 : 0;
                    if (sample.RssKiB > 3L * 1024 * 1024 || excessive >= 3)
                    {
                        stopped = true;
                        step.Cancel();
                    }
                    await Task.Delay(250);
                }
                Observation[] observations = await Task.WhenAll(runs);
                Stopwatch cleanup = Stopwatch.StartNew();
                ResourceSample after;
                while (true)
                {
                    after = await SampleAsync();
                    bool drained;
                    lock (gate) drained = active == 0 && waiting == 0;
                    if ((drained && after.OpenCodeProcesses == 0) || cleanup.Elapsed > TimeSpan.FromSeconds(15)) break;
                    await Task.Delay(100);
                }
                results.Add(new
                {
                    capacity, samples = observations.Length, stopped,
                    errors = observations.Count(item => item.Error),
                    totalP50Ms = Percentile(observations.Select(item => item.TotalMs), .5),
                    totalP95Ms = Percentile(observations.Select(item => item.TotalMs), .95),
                    ttftP50Ms = Percentile(observations.Where(item => item.TtftMs >= 0).Select(item => item.TtftMs), .5),
                    ttftP95Ms = Percentile(observations.Where(item => item.TtftMs >= 0).Select(item => item.TtftMs), .95),
                    queueP50Ms = Percentile(observations.Select(item => item.QueueMs), .5),
                    queueP95Ms = Percentile(observations.Select(item => item.QueueMs), .95),
                    peakActive, peakWaiting, peakProcesses = processes, peakCpuPercent = cpu, peakRssKiB = rss,
                    cleanupMs = cleanup.Elapsed.TotalMilliseconds, after, observations
                });
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { baseline, results }, new JsonSerializerOptions { WriteIndented = true }));
                Assert.Equal(0, active);
                Assert.Equal(0, waiting);
                Assert.Equal(0, after.OpenCodeProcesses);
                Assert.DoesNotContain(Directory.GetDirectories(root, "*", SearchOption.AllDirectories), path => Path.GetFileName(path).StartsWith("t-", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("p-", StringComparison.Ordinal));
                if (stopped) break;
                Assert.All(observations, item => Assert.False(item.Error));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<Observation> RunAsync(StreamingHost host, string id, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        double first = -1, queue = 0;
        bool error = false, done = false;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/agent/chat/stream");
            request.Headers.Add("X-Conversation-Id", id);
            using HttpResponseMessage response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(ct));
            string? kind = null;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) kind = line[7..];
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                using JsonDocument data = JsonDocument.Parse(line[6..]);
                if (kind == "content" && first < 0) first = clock.Elapsed.TotalMilliseconds;
                if (kind == "execution_status" && data.RootElement.GetProperty("queueMilliseconds") is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double value)) queue = value;
                if (kind == "error") error = true;
                if (kind == "done") done = true;
            }
        }
        catch (Exception errorValue) when (errorValue is OperationCanceledException or HttpRequestException or IOException)
        {
            error = true;
        }
        return new(clock.Elapsed.TotalMilliseconds, first, queue, error || !done);
    }

    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        double[] sorted = source.Order().ToArray();
        return sorted.Length == 0 ? -1 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
    }

    private static async Task<ResourceSample> SampleAsync()
    {
        ProcessStartInfo start = new("ps") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-axo");
        start.ArgumentList.Add("pid=,ppid=,%cpu=,rss=,comm=");
        using Process ps = Process.Start(start)!;
        string text = await ps.StandardOutput.ReadToEndAsync();
        await ps.WaitForExitAsync();
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(row => row.Length >= 5).Select(row => new
            {
                Id = int.Parse(row[0], CultureInfo.InvariantCulture), Parent = int.Parse(row[1], CultureInfo.InvariantCulture),
                Cpu = double.Parse(row[2], CultureInfo.InvariantCulture), Rss = long.Parse(row[3], CultureInfo.InvariantCulture),
                Name = Path.GetFileName(row[4])
            }).ToArray();
        HashSet<int> owned = [Environment.ProcessId];
        bool changed;
        do
        {
            changed = false;
            foreach (var row in rows) if (owned.Contains(row.Parent)) changed |= owned.Add(row.Id);
        } while (changed);
        var family = rows.Where(row => owned.Contains(row.Id)).ToArray();
        return new(family.Sum(row => row.Cpu), family.Sum(row => row.Rss), family.Count(row => row.Name == "opencode"));
    }

    private sealed record Observation(double TotalMs, double TtftMs, double QueueMs, bool Error);
    private sealed record ResourceSample(double CpuPercent, long RssKiB, int OpenCodeProcesses);
}
