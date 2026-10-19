using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>
/// The agent host over one transport. It serves the mandatory initialize/new/prompt/cancel
/// surface, tracks active sessions, and enforces one prompt turn per session. All handler
/// registration freezes when the host is constructed, before the read loop starts; the host
/// then runs until EOF, close, or fault (SPEC).
/// </summary>
public sealed class AcpAgentConnection : IAsyncDisposable
{
    private readonly AcpRpcConnection _rpc;
    private readonly AcpAgentBuilder _builder;
    private readonly AcpAgentOptions _options;
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private int _initialized;
    private int _disposed;

    public AcpAgentConnection(IAcpTransport transport, AcpAgentBuilder builder, AcpAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(builder);
        _options = options ?? new AcpAgentOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxSessions);
        _builder = builder;
        builder.ThrowIfIncomplete();
        builder.Freeze();
        _rpc = new AcpRpcConnection(transport, AcpRpcRole.Agent, _options.Rpc);
        _rpc.RegisterRequestHandler("initialize", HandleInitializeAsync);
        _rpc.RegisterRequestHandler("session/new", HandleNewSessionAsync);
        _rpc.RegisterRequestHandler("session/prompt", HandlePromptAsync);
        _rpc.RegisterNotificationHandler("session/cancel", HandleCancelAsync);
    }

    /// <summary>Completes when the underlying link ends for any reason; terminal transport faults surface here.</summary>
    public Task Completion => _rpc.Completion;

    /// <summary>Starts the RPC read loop. Registrations are frozen; call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _rpc.Start();
    }

    /// <summary>Test-only observation of the underlying engine, so role tests can assert a drained host.</summary>
    internal int TrackedTaskCount => _rpc.TrackedTaskCount;

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

    internal Task SendUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        SessionNotification notification = new(sessionId, update);
        return _rpc.NotifyAsync("session/update", AcpJson.ToElement(notification), cancellationToken);
    }

    internal Task<TResponse> SendRequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        return CoreAsync();

        async Task<TResponse> CoreAsync()
        {
            JsonElement result = await _rpc.RequestAsync(method, AcpJson.ToElement(request), cancellationToken).ConfigureAwait(false);
            return AcpJson.FromElement<TResponse>(result);
        }
    }

    private Task<JsonElement?> HandleInitializeAsync(AcpRpcRequest request, CancellationToken cancellationToken)
    {
        _ = AcpJson.FromElement<InitializeRequest>(RequireParameters(request, "initialize"));
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            throw new AcpRpcException(new Error(ErrorCode.InternalError, "The connection is already initialized."));
        }

        // [协议] The agent answers with the latest version it supports; this SDK serves v1.
        InitializeResponse response = new(
            AcpProtocol.Version,
            agentCapabilities: BuildCapabilities(),
            agentInfo: _builder.Info is { } info ? Optional<Implementation>.FromValue(info) : default);
        return Task.FromResult<JsonElement?>(AcpJson.ToElement(response));
    }

    private async Task<JsonElement?> HandleNewSessionAsync(AcpRpcRequest request, CancellationToken cancellationToken)
    {
        RequireInitialized();
        NewSessionRequest newSession = AcpJson.FromElement<NewSessionRequest>(RequireParameters(request, "session/new"));
        if (_sessions.Count >= _options.MaxSessions)
        {
            throw BuildAgentError(
                AcpAgentErrorCode.ResourceLimit,
                "The session limit for this connection is exhausted.",
                nameof(AcpAgentOptions.MaxSessions));
        }

        string sessionId = $"sess-{Guid.NewGuid():N}";
        AgentSession session = new(sessionId);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _sessions[sessionId] = session;
        }

        try
        {
            if (_builder.NewSessionHandler is { } handler)
            {
                AcpNewSessionContext context = new(this, sessionId, newSession.Cwd, newSession.McpServers);
                await handler(context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_lifetime) _sessions.Remove(sessionId);
            throw;
        }

        return AcpJson.ToElement(new NewSessionResponse(sessionId));
    }

    private async Task<JsonElement?> HandlePromptAsync(AcpRpcRequest request, CancellationToken cancellationToken)
    {
        RequireInitialized();
        PromptRequest prompt = AcpJson.FromElement<PromptRequest>(RequireParameters(request, "session/prompt"));
        AgentSession? session;
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            session = _sessions.GetValueOrDefault(prompt.SessionId);
        }

        if (session is null)
        {
            throw new AcpRpcException(new Error(ErrorCode.ResourceNotFound, $"The session '{prompt.SessionId}' does not exist on this connection."));
        }

        ValidatePromptContent(prompt.Prompt);
        Func<AcpPromptContext, CancellationToken, Task<string>> handler = _builder.PromptHandler!;
        CancellationTokenSource cancellation;
        lock (session.Gate)
        {
            if (session.Active is not null)
            {
                throw BuildAgentError(
                    AcpAgentErrorCode.SessionBusy,
                    "The session already has an active prompt turn.",
                    "singlePromptTurn");
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session.Active = cancellation;
        }

        try
        {
            AcpPromptContext context = new(this, prompt.SessionId, prompt.Prompt);
            string stopReason;
            try
            {
                stopReason = await handler(context, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The peer cancelled this turn: a normal outcome, never an internal error (PROTOCOL §5).
                stopReason = StopReason.Cancelled;
            }

            return AcpJson.ToElement(new PromptResponse(stopReason));
        }
        finally
        {
            lock (session.Gate) session.Active = null;
            cancellation.Dispose();
        }
    }

    private Task HandleCancelAsync(AcpRpcNotification notification, CancellationToken cancellationToken)
    {
        CancelNotification cancel = AcpJson.FromElement<CancelNotification>(notification.Parameters!.Value);
        AgentSession? session;
        lock (_lifetime) session = _sessions.GetValueOrDefault(cancel.SessionId);
        if (session is not null)
        {
            lock (session.Gate) session.Active?.Cancel();
        }

        // The cancel notification carries no response; unknown sessions ignore it quietly.
        return Task.CompletedTask;
    }

    /// <summary>Rejects content the host never advertised, so a raw peer cannot push un-agreed payloads through (PROTOCOL §3).</summary>
    private void ValidatePromptContent(IReadOnlyList<ContentBlock> prompt)
    {
        foreach (ContentBlock block in prompt)
        {
            string? type = block.Json.TryGetProperty("type", out JsonElement tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
            bool supported = type switch
            {
                "text" or "resource_link" => true,
                "image" => _builder.PromptCapabilities.Contains("image"),
                "audio" => _builder.PromptCapabilities.Contains("audio"),
                "resource" => _builder.PromptCapabilities.Contains("embeddedContext"),
                _ => false
            };
            if (!supported)
            {
                throw new AcpRpcException(new Error(
                    ErrorCode.InvalidParams,
                    $"The prompt contains a '{type ?? "unknown"}' content block the agent did not negotiate."));
            }
        }
    }

    private Optional<AgentCapabilities> BuildCapabilities()
    {
        // Capabilities come from explicit enables; a declared branch without support never ships (SPEC).
        if (_builder.PromptCapabilities.Count == 0) return default;
        PromptCapabilities prompt = new(
            image: ToFlag("image"),
            audio: ToFlag("audio"),
            embeddedContext: ToFlag("embeddedContext"));
        return Optional<AgentCapabilities>.FromValue(new AgentCapabilities(promptCapabilities: prompt));
    }

    private Optional<bool> ToFlag(string capability) =>
        _builder.PromptCapabilities.Contains(capability) ? Optional<bool>.FromValue(true) : default;

    private void RequireInitialized()
    {
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new AcpRpcException(new Error(ErrorCode.InternalError, "The connection is not initialized; initialize must be the first request."));
        }
    }

    private static JsonElement RequireParameters(AcpRpcRequest request, string method) =>
        request.Parameters ?? throw new AcpRpcException(new Error(ErrorCode.InvalidParams, $"The '{method}' request requires parameters."));

    private static AcpRpcException BuildAgentError(int code, string message, string limit)
    {
        // Namespace and limit are internal constants; no untrusted text enters this JSON.
        string data = $"{{\"{AcpAgentErrorCode.DataNamespace}\":{{\"limit\":\"{limit}\"}}}}";
        return new AcpRpcException(new Error(code, message, Optional<JsonElement>.FromValue(AcpJson.ParseElement(data))));
    }

    private sealed class AgentSession(string id)
    {
        public string Id { get; } = id;
        public object Gate { get; } = new();
        public CancellationTokenSource? Active { get; set; }
    }
}
