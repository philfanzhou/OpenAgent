using System.Collections.Concurrent;
using System.Text;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.TestConsole;

internal sealed class EchoAgent
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = [];
    private static ConfigOption[] Config => [new("model", "Model", "select", AcpJson.Element("echo")) { Category = "model", Options = AcpJson.Parse("""[{"value":"echo","name":"Echo"}]""") }, new("tools", "Tools", "boolean", AcpJson.Parse("false"))];
    private static SessionResponse State(string? id = null) => new(id, new("chat", [new("chat", "Chat"), new("code", "Code")]), Config);
    internal AcpAgentHandlers Handlers => new()
    {
        Initialize = (_, _) => Task.FromResult(new InitializeResponse(AgentCapabilities: new(PromptCapabilities: new(true, true, true), Auth: AcpJson.Parse("""{"logout":{}}""")), AuthMethods: [new("test", "Test identity")], AgentInfo: new("compact-echo", "0.1.0"))),
        NewSession = async (r, context, ct) =>
        {
            string id = Guid.NewGuid().ToString("N");
            _sessions[id] = new(r.Cwd);
            await context.UpdateAsync(id, new InfoUpdate { Title = "Echo session" }, ct);
            return State(id);
        },
        Load = async (r, context, ct) => { Entry entry = Get(r.SessionId); await context.UpdateAsync(r.SessionId, new AgentMessageChunk(new TextContent(entry.Last)), ct); return State(); },
        Resume = (r, _, _) => { Get(r.SessionId); return Task.FromResult(State()); },
        List = (r, _) =>
        {
            int skip = r.Cursor is null ? 0 : int.Parse(r.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            SessionInfo[] all = _sessions.Where(x => r.Cwd is null || x.Value.Cwd == r.Cwd).OrderBy(x => x.Key).Select(x => new SessionInfo(x.Key, x.Value.Cwd)).ToArray();
            return Task.FromResult(new ListSessionsResponse(all.Skip(skip).Take(2).ToArray(), skip + 2 < all.Length ? (skip + 2).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        },
        Close = (_, _) => Task.FromResult(new Empty()),
        Delete = (r, ct) => { _sessions.TryRemove(r.SessionId, out _); return Task.FromResult(new Empty()); },
        SetMode = (r, _) => { Get(r.SessionId); if (r.ModeId is not ("chat" or "code")) { throw new ArgumentException("Invalid mode."); } return Task.FromResult(new Empty()); },
        SetConfig = (r, _) => { Get(r.SessionId); ConfigOption item = Config.First(x => x.Id == r.ConfigId); return Task.FromResult(new SetConfigResponse(Config.Select(x => x.Id == item.Id ? x with { CurrentValue = r.Value } : x).ToArray())); },
        Authenticate = (_, _) => Task.FromResult(new Empty()),
        Logout = (_, _) => Task.FromResult(new Empty()),
        Prompt = PromptAsync
    };
    private Entry Get(string id) => _sessions.TryGetValue(id, out Entry? entry) ? entry : throw new AcpRpcException(-32002, "Session not found.");
    private async Task<PromptResponse> PromptAsync(PromptRequest request, AcpAgentContext context, CancellationToken ct)
    {
        Entry session = Get(request.SessionId);
        string command = string.Concat(request.Prompt.OfType<TextContent>().Select(x => x.Text));
        string text = "Echo: " + command;
        if (command == "slow")
        {
            await context.UpdateAsync(request.SessionId, new AgentMessageChunk(new TextContent("started")), ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
        if (command == "permission")
        {
            PermissionResponse response = await context.RequestPermissionAsync(new(request.SessionId, new("tool-1"), [new("allow", "Allow once", "allow_once"), new("deny", "Reject", "reject_once")]), ct);
            text = response.Outcome.Outcome;
        }
        if (command == "files")
        {
            string path = new Uri(request.Prompt.OfType<ResourceLink>().Single().Uri).LocalPath;
            await context.WriteFileAsync(new(request.SessionId, path, "one\n你好🥐\nthree"), ct);
            text = (await context.ReadFileAsync(new(request.SessionId, path, 2, 1), ct)).Content;
        }
        if (command == "terminal")
        {
            var self = SelfCommand.For("terminal-worker");
            CreateTerminalResponse terminal = await context.CreateTerminalAsync(new(request.SessionId, self.File, self.Args, OutputByteLimit: 8), ct);
            TerminalRequest r = new(request.SessionId, terminal.TerminalId);
            await context.WaitForTerminalAsync(r, ct);
            text = (await context.TerminalOutputAsync(r, ct)).Output;
            await context.KillTerminalAsync(r, ct);
            await context.ReleaseTerminalAsync(r, ct);
        }
        if (command == "elicitation")
        {
            ElicitationResponse result = await context.ElicitAsync(new("Your name?", "form")
            {
                SessionId = request.SessionId,
                RequestedSchema = AcpJson.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""")
            }, ct);
            text = result.Action;
        }
        if (command == "url")
        {
            ElicitationResponse result = await context.ElicitAsync(new("Consent test", "url")
            {
                SessionId = request.SessionId,
                ElicitationId = "e-1",
                Url = "https://example.com/consent"
            }, ct);
            await context.ElicitationCompleteAsync("e-1", ct);
            text = result.Action;
        }
        if (command == "error")
        {
            throw new AcpRpcException(12345, "Test error", AcpJson.Parse("""{"kind":"fixture"}"""));
        }

        if (command == "updates")
        {
            foreach (SessionUpdate update in AllUpdates())
            {
                await context.UpdateAsync(request.SessionId, update, ct);
            }
        }

        if (command.StartsWith("burst:", StringComparison.Ordinal))
        {
            int count = int.Parse(command[6..], System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < count; i++)
            {
                await context.UpdateAsync(request.SessionId, new AgentMessageChunk(new TextContent("x")), ct);
            }

            return new("end_turn");
        }
        session.Last = text;
        foreach (Rune rune in text.EnumerateRunes())
        {
            await context.UpdateAsync(request.SessionId, new AgentMessageChunk(new TextContent(rune.ToString())), ct);
            await Task.Delay(1, ct);
        }
        return new("end_turn");
    }
    internal static SessionUpdate[] AllUpdates() => [
        new UserMessageChunk(new TextContent("user")), new AgentMessageChunk(new TextContent("agent")), new AgentThoughtChunk(new TextContent("thought")),
        new ToolCall("t", "Test"), new ToolCallUpdate("t") { Status = "completed" }, new PlanUpdate([new("Step", "medium", "completed")]),
        new CommandsUpdate([new("help", "Help")]), new ModeUpdate("chat"), new ConfigUpdate(Config), new InfoUpdate { Title = "Updated" },
        new UsageUpdate(10, 100), new NoticeUpdate("info", "Test notice"), new CompactionUpdate("c", "completed"), new CompactionSummaryChunk("c", new TextContent("summary"))];
    private sealed class Entry(string cwd)
    {
        internal string Cwd = cwd; internal string Last = "history";
    }
}
