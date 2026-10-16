using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Transport;
namespace OpenAgent.Runtime.Acp.TestConsole;

internal static class Scenarios
{
    internal static readonly string[] Names = ["framing", "schema", "rpc", "echo", "cancel", "process", "session", "config", "updates", "permission", "files", "terminal", "elicitation", "auth", "fault", "load"];
    internal static async Task<int> RunAsync(string name, bool json)
    {
        if (name != "all" && !Names.Contains(name, StringComparer.Ordinal)) return 2;
        List<ScenarioReport> reports = [];
        foreach (string scenario in name == "all" ? Names : [name])
        {
            Stopwatch timer = Stopwatch.StartNew(); string? error = null;
            try { await ExecuteAsync(scenario).WaitAsync(TimeSpan.FromMinutes(2)); }
            catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
            reports.Add(new(scenario, error is null ? "PASS" : "FAIL", timer.ElapsedMilliseconds, error, scenario == "load" && error is null ? 100000 : null, scenario == "load" ? 10 : null));
            if (!json) Console.WriteLine($"{reports[^1].Status} {scenario} ({timer.ElapsedMilliseconds} ms){(error is null ? "" : ": " + error)}");
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(reports.ToArray(), ReportJsonContext.Default.ScenarioReportArray));
        return reports.Any(x => x.Status == "FAIL") ? 1 : 0;
    }
    private static async Task ExecuteAsync(string name)
    {
        if (name == "framing")
        {
            await using AcpStreamTransport transport = new(new MemoryStream(Encoding.UTF8.GetBytes("{}\r\n[]\n")), new MemoryStream());
            Check(Encoding.UTF8.GetString((await transport.ReadAsync())!) == "{}", "CRLF");
            Check(Encoding.UTF8.GetString((await transport.ReadAsync())!) == "[]", "Second frame"); Check(await transport.ReadAsync() is null, "EOF"); return;
        }
        if (name == "schema")
        {
            foreach (SessionUpdate update in EchoAgent.AllUpdates())
            {
                SessionNotification notification = new("s", update);
                Check(AcpJson.Read<SessionNotification>(AcpJson.Element(notification)).Update.GetType() == update.GetType(), "Update discriminator");
            }
            SessionNotification owned = AcpJson.Read<SessionNotification>(AcpJson.Parse("""{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello"}}}"""));
            Check(ReferenceEquals(owned.Update, owned.Update), "Getter allocates a replacement model");
            ToolCallUpdate patch = new("t") { Title = new Optional<string>(null) };
            JsonElement wire = AcpJson.Element<SessionUpdate>(patch);
            Check(wire.GetProperty("title").ValueKind == JsonValueKind.Null && !wire.TryGetProperty("status", out _), "Patch presence"); return;
        }
        if (name == "process")
        {
            var self = SelfCommand.For("fail-worker"); await using AcpProcess failed = AcpProcess.Start(new(self.File, self.Args));
            Check(await failed.Exit.WaitAsync(TimeSpan.FromSeconds(10)) == 17, "Process exit code");
            try { await using AcpProcess _ = AcpProcess.Start(new("/missing-acp-agent", [])); throw new InvalidOperationException("Missing process accepted"); }
            catch (System.ComponentModel.Win32Exception) { } return;
        }
        string root = Path.Combine(Path.GetTempPath(), "acp-scenario-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await WithClientAsync(name, root); }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static async Task WithClientAsync(string name, string root)
    {
        var self = SelfCommand.For("agent");
        await using AcpProcess process = AcpProcess.Start(new(self.File, self.Args) { WorkingDirectory = root, StandardError = text => Console.Error.Write(text) });
        RootedFileSystem fs = new(_ => [root], allowWrite: true);
        await using LocalTerminalHandler terminal = new(request => request.Command == Environment.ProcessPath);
        AcpClient? client = null;
        AcpClientHandlers handlers = new()
        {
            Permission = async (request, ct) =>
            {
                if (name == "rpc") _ = await client!.ListSessionsAsync(ct: ct);
                return name == "permission" ? PermissionResponse.Cancelled : PermissionResponse.Selected("allow");
            },
            ReadFile = fs.ReadAsync, WriteFile = fs.WriteAsync, Terminal = terminal,
            FormElicitation = true, UrlElicitation = true,
            Elicitation = (request, _) => Task.FromResult(new ElicitationResponse(request.Mode == "form" ? "accept" : "cancel", request.Mode == "form" ? AcpJson.Parse("""{"name":"fixture"}""") : null))
        };
        await using (client = new(process.Transport, handlers, new() { MaxQueuedUpdates = 8192 }))
        {
            await client.InitializeAsync(new("test-console", "0.1.0"));
            if (name == "load")
            {
                await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
                {
                    await using SessionProbe probe = new(await client.NewSessionAsync(new(root, [])));
                    await probe.PromptAsync("burst:10000"); await probe.FinishAsync();
                    Check(probe.Updates.Count(x => x.Update is AgentMessageChunk) == 10000, "Dropped load updates");
                })); return;
            }
            if (name == "auth") { await client.AuthenticateAsync("test"); await client.LogoutAsync(); return; }
            await using SessionProbe session = new(await client.NewSessionAsync(new(root, [])));
            string id = session.Session.SessionId;
            if (name == "session")
            {
                await session.PromptAsync("history"); await session.FinishAsync();
                ListSessionsResponse listed = await client.ListSessionsAsync(); Check(listed.Sessions.Any(x => x.SessionId == id), "List");
                await using (SessionProbe loaded = new(await client.LoadSessionAsync(new(id, root, []))))
                { await loaded.FinishAsync(); Check(loaded.Text.Contains("history", StringComparison.Ordinal), "Load replay"); }
                await using (SessionProbe resumed = new(await client.ResumeSessionAsync(new(id, root, []))))
                { await resumed.FinishAsync(); Check(resumed.Updates.Count == 0, "Resume replayed history"); }
                await client.DeleteSessionAsync(id); await client.DeleteSessionAsync(id);
                Check(!(await client.ListSessionsAsync()).Sessions.Any(x => x.SessionId == id), "Delete"); return;
            }
            if (name == "config")
            { await session.Session.SetModeAsync("code"); SetConfigResponse configured = await session.Session.SetConfigAsync("tools", AcpJson.Parse("true")); Check(configured.ConfigOptions.Single(x => x.Id == "tools").CurrentValue.GetBoolean(), "Config"); return; }
            if (name == "cancel")
            {
                await using AcpPromptTurn turn = await session.Session.StartPromptAsync([new TextContent("slow")]);
                await turn.CancelAndWaitAsync(); Check((await turn.Completion).StopReason == "cancelled", "Cancellation confirmation");
                await session.PromptAsync("reuse"); return;
            }
            if (name == "fault")
            {
                try { await session.PromptAsync("error"); throw new InvalidOperationException("Remote error lost"); }
                catch (AcpRpcException error) { Check(error.Code == 12345 && error.ErrorData?.GetProperty("kind").GetString() == "fixture", "Remote error mapping"); } return;
            }
            ContentBlock[] blocks = name == "files" ? [new TextContent("files"), new ResourceLink(new Uri(Path.Combine(root, "test.txt")).AbsoluteUri, "file")] : [new TextContent(name switch { "echo" => "你好🥐", "rpc" => "permission", _ => name })];
            await session.PromptAsync(blocks); await session.FinishAsync();
            if (name == "echo") Check(session.Text == "Echo: 你好🥐", "Unicode echo");
            if (name == "rpc") Check(session.Text == "selected", "Nested reverse request");
            if (name == "permission") Check(session.Text == "cancelled", "Permission denial");
            if (name == "files") Check(session.Text == "你好🥐", "File reverse callbacks");
            if (name == "terminal") Check(session.Text == "好🥐", "UTF-8 output limit");
            if (name == "elicitation") Check(session.Text == "accept", "Form response");
            if (name == "updates") Check(session.Updates.Select(x => x.Update.GetType()).Distinct().Count() == 14, "Update coverage");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class SessionProbe : IAsyncDisposable
    {
        internal AcpSession Session { get; }
        internal List<SessionNotification> Updates { get; } = [];
        internal string Text => string.Concat(Updates.Select(x => x.Update).OfType<AgentMessageChunk>().Select(x => x.Content).OfType<TextContent>().Select(x => x.Text));
        private readonly Task _consume;
        internal SessionProbe(AcpSession session) { Session = session; _consume = ConsumeAsync(); }
        private async Task ConsumeAsync() { await foreach (SessionNotification update in Session.ReadUpdatesAsync()) Updates.Add(update); }
        internal Task<PromptResponse> PromptAsync(string text) => PromptAsync([new TextContent(text)]);
        internal async Task<PromptResponse> PromptAsync(ContentBlock[] content)
        { await using AcpPromptTurn turn = await Session.StartPromptAsync(content); return await turn.Completion; }
        internal async Task FinishAsync() { await Session.DisposeAsync(); await _consume; }
        public ValueTask DisposeAsync() => new(FinishAsync());
    }
}
internal sealed record ScenarioReport(string Scenario, string Status, long ElapsedMilliseconds, string? Error, long? UpdateCount = null, int? Sessions = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScenarioReport[]))]
internal partial class ReportJsonContext : JsonSerializerContext;
