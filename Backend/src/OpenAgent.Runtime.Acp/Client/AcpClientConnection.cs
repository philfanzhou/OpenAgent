using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Client;

/// <summary>
/// The client role over one transport: initialize negotiation, session creation, and the
/// session/update stream. Starting the connection only starts RPC — initialize stays an
/// explicit call (SPEC: Connect never implicitly initializes).
/// </summary>
/// <remarks>
/// Engine registrations freeze in the constructor, before <see cref="Start"/> opens the read
/// loop. Updates route by sessionId; updates that arrive before a session/new response are
/// buffered under the session id and handed over at binding (SPEC: bounded pre-response cache).
/// </remarks>
public sealed class AcpClientConnection : IAsyncDisposable
{
    private readonly AcpRpcConnection _rpc;
    private readonly AcpClientOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _bindGate = new();
    private readonly List<SessionNotification> _unbound = [];
    private readonly object _initGate = new();
    private InitializeResponse? _negotiated;
    private bool _initializing;
    private int _disposed;

    public AcpClientConnection(IAcpTransport transport, AcpClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _options = options ?? new AcpClientOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxQueuedUpdates);
        _rpc = new AcpRpcConnection(transport, AcpRpcRole.Client, _options.Rpc);
        _rpc.RegisterRequestHandler("session/request_permission", DenyPermissionAsync);
        _rpc.RegisterNotificationHandler("session/update", RouteUpdateAsync);
    }

    /// <summary>Completes when the underlying link ends for any reason; terminal transport faults surface here.</summary>
    public Task Completion => _rpc.Completion;

    /// <summary>True once initialize negotiated successfully and the link is still open.</summary>
    public bool IsReady => Volatile.Read(ref _disposed) == 0 && _negotiated is not null && !_lifetime.IsCancellationRequested;

    /// <summary>The negotiated initialize response, or null before initialize completes.</summary>
    public InitializeResponse? Negotiated => _negotiated;

    /// <summary>Starts the RPC engine. Engine registrations are already frozen; call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _rpc.Start();
        _ = _rpc.Completion.ContinueWith(
            static (_, state) => ((AcpClientConnection)state!).OnConnectionClosed(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Runs the initialize exchange and stores the negotiation. The SDK proposes and accepts
    /// only v1: any other version answered by the agent closes the connection and throws
    /// <see cref="AcpCapabilityException"/> (PROTOCOL §2). A second call on the same
    /// connection fails; sessions cannot be created before initialize completes.
    /// </summary>
    public async Task<InitializeResponse> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_initGate)
        {
            if (_negotiated is not null) throw new InvalidOperationException("The connection is already initialized.");
            if (_initializing) throw new InvalidOperationException("An initialize call is already in progress.");
            _initializing = true;
        }

        try
        {
            if (_lifetime.IsCancellationRequested) throw new AcpConnectionClosedException("the connection closed before initialize");
            InitializeRequest request = new(
                AcpProtocol.Version,
                new ClientCapabilities(),
                clientInfo: _options.ClientInfo is { } info ? Optional<Implementation>.FromValue(info) : default);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(_options.InitializeTimeout);
            JsonElement result = await _rpc.RequestAsync("initialize", AcpJson.ToElement(request), linked.Token).ConfigureAwait(false);
            InitializeResponse response = AcpJson.FromElement<InitializeResponse>(result);
            if (response.ProtocolVersion != AcpProtocol.Version)
            {
                await DisposeAsync().ConfigureAwait(false);
                throw new AcpCapabilityException(
                    $"The agent answered protocol version {response.ProtocolVersion}; this SDK only negotiates v{AcpProtocol.Version}.");
            }

            _negotiated = response;
            return response;
        }
        finally
        {
            lock (_initGate) _initializing = false;
        }
    }

    /// <summary>
    /// Creates one session. Requires a completed initialize. The cwd must be an absolute path;
    /// updates that arrived before the response are bound to the session when it returns.
    /// </summary>
    public async Task<AcpSession> NewSessionAsync(string cwd, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        if (!Path.IsPathFullyQualified(cwd)) throw new ArgumentException("The session cwd must be an absolute path.", nameof(cwd));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_negotiated is null) throw new InvalidOperationException("The connection is not initialized; call InitializeAsync first.");
        if (_lifetime.IsCancellationRequested) throw new AcpConnectionClosedException("the connection closed before session/new");

        NewSessionRequest request = new(cwd, []);
        JsonElement result = await _rpc.RequestAsync("session/new", AcpJson.ToElement(request), cancellationToken).ConfigureAwait(false);
        NewSessionResponse response = AcpJson.FromElement<NewSessionResponse>(result);
        if (string.IsNullOrWhiteSpace(response.SessionId))
        {
            // The wire schema cannot express a minimum length; a blank id would create a session
            // no update could ever route to, so it fails locally before binding.
            throw new AcpCapabilityException("The agent returned a blank session id; the session cannot be addressed.");
        }

        AcpSession session = new(this, response.SessionId, _options.MaxQueuedUpdates);
        BindSession(session);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose is idempotent: repeat calls await the same engine close instead of touching a disposed token source.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            await _rpc.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            return;
        }

        await _rpc.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Sends session/cancel; completes once the notification is on the wire. The final prompt response confirms the cancel.</summary>
    internal Task SendCancelAsync(string sessionId, CancellationToken cancellationToken) =>
        _rpc.NotifyAsync("session/cancel", AcpJson.ToElement(new CancelNotification(sessionId)), cancellationToken);

    internal int MaxQueuedUpdates => _options.MaxQueuedUpdates;

    /// <summary>Test-only observation of the underlying engine, so role tests can assert a drained connection.</summary>
    internal int TrackedTaskCount => _rpc.TrackedTaskCount;

    internal Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken) =>
        SendAsync<PromptRequest, PromptResponse>("session/prompt", request, cancellationToken);

    /// <summary>Role-layer turn barrier: unblocks once queued updates finished dispatching, so turn completion reports only enqueued updates.</summary>
    internal Task AwaitUpdateDrainAsync(CancellationToken cancellationToken) => _rpc.AwaitNotificationDrainAsync(cancellationToken);

    /// <summary>Rejects prompt content the negotiated agent capabilities do not cover, before anything reaches the wire.</summary>
    internal void ValidatePromptContent(IReadOnlyList<ContentBlock> prompt)
    {
        InitializeResponse negotiated = _negotiated
            ?? throw new InvalidOperationException("The connection is not initialized; call InitializeAsync first.");
        PromptCapabilities capabilities = negotiated.AgentCapabilitiesOrDefault.PromptCapabilitiesOrDefault;
        foreach (ContentBlock block in prompt)
        {
            string? type = block.Json.TryGetProperty("type", out JsonElement tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
            bool supported = type switch
            {
                "text" or "resource_link" => true,
                "image" => capabilities.ImageOrDefault,
                "audio" => capabilities.AudioOrDefault,
                "resource" => capabilities.EmbeddedContextOrDefault,
                _ => false
            };
            if (!supported)
            {
                throw new AcpCapabilityException(
                    $"The agent did not negotiate prompt support for the '{type ?? "unknown"}' content block.");
            }
        }
    }

    internal void ThrowIfClosed(string operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_lifetime.IsCancellationRequested) throw new AcpConnectionClosedException($"the connection closed before {operation}");
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        JsonElement result = await _rpc.RequestAsync(method, AcpJson.ToElement(request), cancellationToken).ConfigureAwait(false);
        return AcpJson.FromElement<TResponse>(result);
    }

    /// <summary>P3 default: every permission request is answered cancelled; nothing is auto-approved (SPEC).</summary>
    private Task<JsonElement?> DenyPermissionAsync(AcpRpcRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<JsonElement?>(AcpJson.ToElement(new RequestPermissionResponse(new RequestPermissionOutcome(new RequestPermissionOutcomeCancelled()))));

    private Task RouteUpdateAsync(AcpRpcNotification notification, CancellationToken cancellationToken)
    {
        // The engine validated the session/update payload before dispatch, so binding cannot fail here.
        SessionNotification update = AcpJson.FromElement<SessionNotification>(notification.Parameters!.Value);
        lock (_bindGate)
        {
            AcpSession? session = _sessions.GetValueOrDefault(update.SessionId);
            if (session is null)
            {
                if (_unbound.Count >= _options.MaxQueuedUpdates)
                {
                    // Slow consumption or an unbindable flood must fault the connection instead of
                    // dropping fragments or stalling the reader (SPEC: bounded early-event cache).
                    _ = DisposeAsync();
                    throw new AcpQuotaExceededException(nameof(AcpClientOptions.MaxQueuedUpdates), _options.MaxQueuedUpdates, _options.MaxQueuedUpdates + 1L);
                }

                _unbound.Add(update);
                return Task.CompletedTask;
            }

            // The dispatch loop and BindSession enqueue under the same gate, so a session's
            // update stream always matches wire order across the binding boundary (review F1).
            session.Enqueue(update);
            return Task.CompletedTask;
        }
    }

    /// <summary>Registers the session, moves and enqueues its pre-response updates under one lock. The dispatch loop enqueues under the same gate, so handoff keeps wire order.</summary>
    private void BindSession(AcpSession session)
    {
        lock (_bindGate)
        {
            _sessions[session.SessionId] = session;
            for (int index = 0; index < _unbound.Count; )
            {
                if (_unbound[index].SessionId == session.SessionId)
                {
                    session.Enqueue(_unbound[index]);
                    _unbound.RemoveAt(index);
                }
                else
                {
                    index++;
                }
            }
        }
    }

    private void OnConnectionClosed()
    {
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }

        AcpSession[] sessions;
        lock (_bindGate)
        {
            sessions = [.. _sessions.Values];
            _unbound.Clear();
        }

        foreach (AcpSession session in sessions) session.OnConnectionClosed();
    }
}
