using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Transport;
namespace OpenAgent.Runtime.Acp.Client;

public sealed class AcpClient : IAsyncDisposable
{
    private readonly AcpClientHandlers _handlers;
    private readonly Dictionary<string, SessionFeed> _feeds = [];
    private readonly object _gate = new();
    private long _bufferedBytes;
    private int _initializing;
    private int _opening;
    private readonly Dictionary<string, string?> _elicitations = [];
    public AcpRpcConnection Connection { get; }
    public InitializeResponse? Negotiated { get; private set; }
    public AcpClient(IAcpTransport transport, AcpClientHandlers? handlers = null, AcpConnectionOptions? options = null)
    {
        _handlers = handlers ?? new();
        if (_handlers.Elicitation is null && (_handlers.FormElicitation || _handlers.UrlElicitation)) throw new ArgumentException("Elicitation handler required.");
        Connection = new(transport, options);
        Connection.OnNotification<SessionNotification>("session/update", Receive);
        Connection.Closed += failure => { lock (_gate) foreach (SessionFeed feed in _feeds.Values) feed.Queue.Writer.TryComplete(failure); };
        Connection.On<RequestPermissionRequest, PermissionResponse>("session/request_permission", async (request, ct) =>
        {
            PermissionResponse response;
            try { response = _handlers.Permission is null ? PermissionResponse.Cancelled : await _handlers.Permission(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return PermissionResponse.Cancelled; }
            if (response.Outcome.Outcome is not ("selected" or "cancelled") || response.Outcome.Outcome == "selected" && !request.Options.Any(x => x.OptionId == response.Outcome.OptionId))
                throw new ArgumentException("Invalid permission option.");
            return response;
        });
        if (_handlers.ReadFile is not null) Connection.On<ReadFileRequest, ReadFileResponse>("fs/read_text_file", _handlers.ReadFile);
        if (_handlers.WriteFile is not null) Connection.On<WriteFileRequest, Empty>("fs/write_text_file", _handlers.WriteFile);
        if (_handlers.Terminal is { } terminal)
        {
            Connection.On<CreateTerminalRequest, CreateTerminalResponse>("terminal/create", terminal.CreateAsync);
            Connection.On<TerminalRequest, TerminalOutputResponse>("terminal/output", terminal.OutputAsync);
            Connection.On<TerminalRequest, ExitStatus>("terminal/wait_for_exit", terminal.WaitAsync);
            Connection.On<TerminalRequest, Empty>("terminal/kill", terminal.KillAsync);
            Connection.On<TerminalRequest, Empty>("terminal/release", terminal.ReleaseAsync);
        }
        if (_handlers.Elicitation is not null)
        {
            Connection.On<ElicitationRequest, ElicitationResponse>("elicitation/create", ElicitAsync);
            Connection.OnNotification<ElicitationComplete>("elicitation/complete", e =>
            {
                // 只接受本连接已登记的 URL 流程，重复或未知完成通知不得触发业务回调。
                bool known; lock (_gate) known = _elicitations.Remove(e.ElicitationId);
                if (known) _handlers.ElicitationComplete?.Invoke(e);
            });
        }
        _handlers.ConfigureConnection?.Invoke(Connection);
        Connection.Start();
    }

    public async Task<InitializeResponse> InitializeAsync(Implementation? info = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initializing, 1) != 0) throw new InvalidOperationException("Initialize only once.");
        Dictionary<string, Empty> elicitation = [];
        if (_handlers.FormElicitation) elicitation.Add("form", new());
        if (_handlers.UrlElicitation) elicitation.Add("url", new());
        ClientCapabilities caps = new(new(_handlers.ReadFile is not null, _handlers.WriteFile is not null), _handlers.Terminal is not null,
            AcpJson.Parse("""{"configOptions":{"boolean":{}},"notices":{},"compaction":{}}"""),
            Auth: _handlers.TerminalAuthentication is null ? null : AcpJson.Parse("""{"terminal":{}}"""),
            Elicitation: _handlers.Elicitation is null ? null : AcpJson.Element(elicitation));
        Negotiated = await Connection.RequestAsync<InitializeRequest, InitializeResponse>("initialize", new(1, caps, info), cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (Negotiated.ProtocolVersion != 1) { await DisposeAsync().ConfigureAwait(false); throw new NotSupportedException("Unsupported ACP version."); }
        return Negotiated;
    }
    public async Task<Empty> AuthenticateAsync(string methodId, CancellationToken ct = default)
    {
        Ready(); AuthMethod method = (Negotiated!.AuthMethods ?? []).FirstOrDefault(x => x.Id == methodId) ?? throw new ArgumentException("Unknown authentication method.");
        // terminal 认证由宿主单独交互启动，协议禁止将它发送到 authenticate。
        if (method.Type == "terminal")
        {
            Require(_handlers.TerminalAuthentication is not null);
            if (await _handlers.TerminalAuthentication!(method, ct).ConfigureAwait(false) != 0) throw new AcpRpcException(-32000, "Terminal authentication failed.");
            return new();
        }
        Require(method.Type is null or "agent");
        return await Connection.RequestAsync<AuthenticateRequest, Empty>("authenticate", new(methodId), ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    }
    public Task<Empty> LogoutAsync(CancellationToken ct = default)
    { Ready(); Require(Negotiated!.AgentCapabilities?.Auth is { } a && a.TryGetProperty("logout", out _)); return Connection.RequestAsync<Empty, Empty>("logout", new(), ct); }
    public async Task<AcpSession> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default)
    {
        Ready(); ValidateRoots(request.Cwd, request.AdditionalDirectories); RequireAdditionalRoots(request.AdditionalDirectories);
        ReserveOpening();
        try
        {
            SessionResponse response = await Connection.RequestAsync<NewSessionRequest, SessionResponse>("session/new", request, ct).ConfigureAwait(false);
            return Bind(response.SessionId ?? throw new JsonException("Missing sessionId."), response);
        }
        finally { lock (_gate) _opening--; }
    }
    public async Task<AcpSession> LoadSessionAsync(LoadSessionRequest request, CancellationToken ct = default)
    {
        Ready(); Require(Negotiated!.AgentCapabilities?.LoadSession == true); ValidateRoots(request.Cwd, request.AdditionalDirectories); RequireAdditionalRoots(request.AdditionalDirectories);
        ReserveOpening();
        try
        {
            SessionResponse response = await Connection.RequestAsync<LoadSessionRequest, SessionResponse>("session/load", request, ct).ConfigureAwait(false);
            return Bind(request.SessionId, response);
        }
        finally { lock (_gate) _opening--; }
    }
    public async Task<AcpSession> ResumeSessionAsync(LoadSessionRequest request, CancellationToken ct = default)
    {
        Ready(); Require(Negotiated!.AgentCapabilities?.SessionCapabilities?.Resume is not null); ValidateRoots(request.Cwd, request.AdditionalDirectories); RequireAdditionalRoots(request.AdditionalDirectories);
        ReserveOpening();
        try
        {
            SessionResponse response = await Connection.RequestAsync<LoadSessionRequest, SessionResponse>("session/resume", request, ct).ConfigureAwait(false);
            return Bind(request.SessionId, response);
        }
        finally { lock (_gate) _opening--; }
    }
    public Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest? request = null, CancellationToken ct = default)
    { Ready(); Require(Negotiated!.AgentCapabilities?.SessionCapabilities?.List is not null); return Connection.RequestAsync<ListSessionsRequest, ListSessionsResponse>("session/list", request ?? new(), ct); }
    public async Task<Empty> DeleteSessionAsync(string id, CancellationToken ct = default)
    {
        Ready(); Require(Negotiated!.AgentCapabilities?.SessionCapabilities?.Delete is not null);
        Empty response = await Connection.RequestAsync<SessionRequest, Empty>("session/delete", new(id), ct).ConfigureAwait(false);
        await ReleaseAsync(id).ConfigureAwait(false); return response;
    }
    private async Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken ct)
    {
        if (request.Mode == "form" && !_handlers.FormElicitation || request.Mode == "url" && !_handlers.UrlElicitation) throw new AcpRpcException(-32601, "Elicitation mode not supported.");
        if (request.Mode is not ("form" or "url")) return new("cancel");
        if ((request.SessionId is null) == (request.RequestId is null)) throw new ArgumentException("Exactly one elicitation scope is required.");
        if (request.Mode == "form" && request.RequestedSchema is null) throw new ArgumentException("Form schema is required.");
        if (request.Mode == "url")
        {
            if (string.IsNullOrEmpty(request.ElicitationId) || !Uri.TryCreate(request.Url, UriKind.Absolute, out _)) throw new ArgumentException("Invalid URL elicitation.");
            lock (_gate)
            {
                if (_elicitations.Count >= Connection.Options.MaxPendingRequests || !_elicitations.TryAdd(request.ElicitationId, request.SessionId)) throw new ArgumentException("Duplicate or excessive elicitation.");
            }
        }
        ElicitationResponse response;
        try { response = await _handlers.Elicitation!(request, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { response = new("cancel"); }
        catch { if (request.ElicitationId is { } id) { lock (_gate) _elicitations.Remove(id); } throw; }
        if (response.Action is not ("accept" or "decline" or "cancel")) throw new ArgumentException("Invalid elicitation action.");
        if (request.Mode == "form" && response.Action == "accept") ElicitationForm.Validate(request.RequestedSchema!.Value, response.Content);
        if (request.Mode == "url" && response.Content is not null || response.Action != "accept" && response.Content is not null) throw new ArgumentException("Unexpected elicitation content.");
        return response;
    }
    internal static void Require(bool enabled) { if (!enabled) throw new NotSupportedException("ACP capability not advertised."); }
    internal static void ValidateRoots(string cwd, string[]? roots)
    { if (!Path.IsPathFullyQualified(cwd) || (roots ?? []).Any(x => !Path.IsPathFullyQualified(x))) throw new ArgumentException("Workspace roots must be absolute."); }
    private void RequireAdditionalRoots(string[]? roots) { if (roots is { Length: > 0 }) Require(Negotiated!.AgentCapabilities?.SessionCapabilities?.AdditionalDirectories is not null); }
    private void Ready() { if (Negotiated is null) throw new InvalidOperationException("Initialize before creating sessions."); }
    private void ReserveOpening()
    {
        lock (_gate)
        {
            if (_feeds.Values.Count(x => x.Bound) + _opening >= Connection.Options.MaxSessions) throw new InvalidOperationException("MaxSessions exceeded.");
            _opening++;
        }
    }
    private AcpSession Bind(string id, SessionResponse response)
    {
        lock (_gate)
        {
            SessionFeed feed = GetFeed(id);
            if (feed.Bound) throw new InvalidOperationException("Session already bound.");
            feed.Bound = true;
            return new(this, id, response, feed);
        }
    }
    private SessionFeed GetFeed(string id)
    {
        if (_feeds.TryGetValue(id, out SessionFeed? feed)) return feed;
        if (_feeds.Count >= Connection.Options.MaxSessions) throw new InvalidOperationException("MaxSessions exceeded.");
        feed = new(Connection.Options.MaxQueuedUpdates); _feeds.Add(id, feed); return feed;
    }
    private void Receive(SessionNotification notification)
    {
        // new/load 的响应之前也可能收到更新：先按 sessionId 缓存，不绑定“当前 prompt”。
        lock (_gate)
        {
            SessionFeed feed = GetFeed(notification.SessionId);
            int bytes = Encoding.UTF8.GetByteCount(AcpJson.Element(notification).GetRawText());
            _bufferedBytes += bytes; feed.BufferedBytes += bytes;
            if (_bufferedBytes > Connection.Options.MaxBufferedBytes || !feed.Queue.Writer.TryWrite((notification, bytes)))
                throw new IOException("ACP update buffer exceeded.");
        }
    }
    internal void Consumed(SessionFeed feed, int bytes)
    { lock (_gate) if (!feed.Released) { _bufferedBytes -= bytes; feed.BufferedBytes -= bytes; } }
    internal async Task ReleaseAsync(string id)
    {
        if (_handlers.Terminal is not null) await _handlers.Terminal.CloseSessionAsync(id).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (string key in _elicitations.Where(x => x.Value == id).Select(x => x.Key).ToArray()) _elicitations.Remove(key);
        }
        lock (_gate) if (_feeds.Remove(id, out SessionFeed? feed))
        {
            // Complete 允许消费者读完尾部；清理者不能与消费者竞争 TryRead，否则会静默丢事件。
            _bufferedBytes -= feed.BufferedBytes; feed.BufferedBytes = 0; feed.Released = true;
            feed.Queue.Writer.TryComplete();
        }
    }
    public async ValueTask DisposeAsync()
    {
        string[] ids; lock (_gate) ids = _feeds.Keys.ToArray();
        await Connection.DisposeAsync().ConfigureAwait(false);
        foreach (string id in ids) await ReleaseAsync(id).ConfigureAwait(false);
    }
    internal sealed class SessionFeed(int capacity)
    {
        internal Channel<(SessionNotification Notification, int Bytes)> Queue { get; } = Channel.CreateBounded<(SessionNotification, int)>(new BoundedChannelOptions(capacity) { SingleWriter = true, SingleReader = true });
        internal bool Bound;
        internal bool Released;
        internal long BufferedBytes;
    }
}
