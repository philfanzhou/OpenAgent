using System.Collections.Concurrent;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>Agent 角色宿主。负责握手、会话并发和协议分派，业务状态由 handlers 持有。</summary>
public sealed class AcpAgentHost : IAsyncDisposable
{
    private readonly AcpAgentHandlers _handlers;
    private readonly ConcurrentDictionary<string, SessionRun> _sessions = [];
    private InitializeResponse? _negotiated;
    private AcpAgentContext? _context;
    private int _initializing;
    private int _sessionCount;
    public AcpRpcConnection Connection
    {
        get;
    }
    public AcpAgentHost(IAcpTransport transport, AcpAgentHandlers handlers, AcpConnectionOptions? options = null)
    {
        _handlers = handlers;
        Connection = new(transport, options);
        Connection.On<InitializeRequest, InitializeResponse>("initialize", async (request, ct) =>
        {
            if (Interlocked.Exchange(ref _initializing, 1) != 0)
            {
                throw new AcpRpcException(-32600, "Already initialized.");
            }

            InitializeResponse result = await handlers.Initialize(request, ct).ConfigureAwait(false);
            if (request.ProtocolVersion != 1)
            {
                return result with
                {
                    ProtocolVersion = 1
                };
            }

            if (result.ProtocolVersion != 1)
            {
                throw new ArgumentException("Handler must implement ACP version 1.");
            }

            if (result.AuthMethods?.Any(x => x.Type is null or "agent") == true && handlers.Authenticate is null || result.AgentCapabilities?.Auth is { } auth && auth.TryGetProperty("logout", out _) && handlers.Logout is null)
            {
                throw new ArgumentException("Advertised auth capability has no handler.");
            }

            if (result.AuthMethods?.Any(x => x.Type == "terminal") == true && !(request.ClientCapabilities?.Auth is { } clientAuth && clientAuth.TryGetProperty("terminal", out _)))
            {
                throw new ArgumentException("Client does not support terminal authentication.");
            }

            AgentCapabilities caps = result.AgentCapabilities ?? new();
            if (caps.LoadSession && handlers.Load is null || caps.SessionCapabilities?.Close is not null && handlers.Close is null || caps.SessionCapabilities?.Resume is not null && handlers.Resume is null || caps.SessionCapabilities?.List is not null && handlers.List is null || caps.SessionCapabilities?.Delete is not null && handlers.Delete is null)
            {
                throw new ArgumentException("Advertised capability has no handler.");
            }

            _context = new(Connection, request.ClientCapabilities ?? new());
            _negotiated = result with
            {
                AgentCapabilities = caps with
                {
                    LoadSession = handlers.Load is not null,
                    SessionCapabilities = new(handlers.List is null ? null : new(), handlers.Delete is null ? null : new(), handlers.Resume is null ? null : new(), handlers.Close is null ? null : new(), caps.SessionCapabilities?.AdditionalDirectories)
                }
            };
            return _negotiated;
        });
        Connection.On<NewSessionRequest, SessionResponse>("session/new", async (request, ct) =>
        {
            Ready();
            AcpClient.ValidateRoots(request.Cwd, request.AdditionalDirectories);
            CheckCapacity();
            try
            {
                SessionResponse result = await handlers.NewSession(request, _context!, ct).ConfigureAwait(false);
                Add(result.SessionId ?? throw new ArgumentException("Missing sessionId."));
                return result;
            }
            catch
            {
                Interlocked.Decrement(ref _sessionCount);
                throw;
            }
        });
        if (handlers.Load is not null)
        {
            Connection.On<LoadSessionRequest, SessionResponse>("session/load", (r, ct) => RestoreAsync(r, handlers.Load, ct));
        }

        if (handlers.Resume is not null)
        {
            Connection.On<LoadSessionRequest, SessionResponse>("session/resume", (r, ct) => RestoreAsync(r, handlers.Resume, ct));
        }

        if (handlers.List is not null)
        {
            Connection.On<ListSessionsRequest, ListSessionsResponse>("session/list", (r, ct) => { Ready(); return handlers.List(r, ct); });
        }

        if (handlers.Delete is not null)
        {
            Connection.On<SessionRequest, Empty>("session/delete", async (r, ct) => { Ready(); await CloseLocalAsync(r.SessionId).ConfigureAwait(false); return await handlers.Delete(r, ct).ConfigureAwait(false); });
        }

        if (handlers.Close is not null)
        {
            Connection.On<SessionRequest, Empty>("session/close", async (r, ct) => { Ready(); await CloseLocalAsync(r.SessionId).ConfigureAwait(false); return await handlers.Close(r, ct).ConfigureAwait(false); });
        }

        if (handlers.SetMode is not null)
        {
            Connection.On<SetModeRequest, Empty>("session/set_mode", (r, ct) => { Get(r.SessionId); return handlers.SetMode(r, ct); });
        }

        if (handlers.SetConfig is not null)
        {
            Connection.On<SetConfigRequest, SetConfigResponse>("session/set_config_option", (r, ct) => { Get(r.SessionId); return handlers.SetConfig(r, ct); });
        }

        if (handlers.Authenticate is not null)
        {
            Connection.On<AuthenticateRequest, Empty>("authenticate", (r, ct) => { Ready(); if (!(_negotiated!.AuthMethods ?? []).Any(x => x.Id == r.MethodId && x.Type is null or "agent")) { throw new ArgumentException("Unknown auth method."); } return handlers.Authenticate(r, ct); });
        }

        if (handlers.Logout is not null)
        {
            Connection.On<Empty, Empty>("logout", (r, ct) => { Ready(); return handlers.Logout(r, ct); });
        }

        Connection.On<PromptRequest, PromptResponse>("session/prompt", PromptAsync);
        Connection.OnNotification<SessionRequest>("session/cancel", request => Cancel(request.SessionId));
        handlers.ConfigureConnection?.Invoke(Connection);
        Connection.Start();
    }
    private async Task<SessionResponse> RestoreAsync(LoadSessionRequest request, Func<LoadSessionRequest, AcpAgentContext, CancellationToken, Task<SessionResponse>> restore, CancellationToken ct)
    {
        Ready();
        AcpClient.ValidateRoots(request.Cwd, request.AdditionalDirectories);
        CheckCapacity();
        try
        {
            if (_sessions.ContainsKey(request.SessionId))
            {
                throw new ArgumentException("Session already active.");
            }

            SessionResponse response = await restore(request, _context!, ct).ConfigureAwait(false);
            Add(request.SessionId);
            return response;
        }
        catch
        {
            Interlocked.Decrement(ref _sessionCount);
            throw;
        }
    }
    // Busy 与 Done 在同一临界区发布，close 才不会等待旧的完成信号。
    private async Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken ct)
    {
        SessionRun run = Get(request.SessionId);
        lock (run)
        {
            if (run.Busy != 0)
            {
                throw new AcpRpcException(10001, "Session busy.");
            }

            run.Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            run.Busy = 1;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            PromptCapabilities? caps = _negotiated!.AgentCapabilities?.PromptCapabilities;
            foreach (ContentBlock content in request.Prompt)
            {
                if (content is ImageContent && caps?.Image != true || content is AudioContent && caps?.Audio != true || content is EmbeddedResource && caps?.EmbeddedContext != true)
                {
                    throw new ArgumentException("Unsupported prompt content.");
                }
            }

            return await _handlers.Prompt(request, _context!, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new("cancelled");
        }
        finally
        {
            lock (run) { run.Busy = 0;
            run.Done.TrySetResult();
            }
        }
    }
    private void Cancel(string id) => Connection.CancelRequests((method, p) => method == "session/prompt" && p.TryGetProperty("sessionId", out JsonElement session) && session.GetString() == id);
    private async Task CloseLocalAsync(string id)
    {
        Cancel(id);
        if (_sessions.TryRemove(id, out SessionRun? run))
        {
            Interlocked.Decrement(ref _sessionCount);
            Task done;
            lock (run)
            {
                done = run.Busy != 0 ? run.Done.Task : Task.CompletedTask;
            }

            await done.WaitAsync(Connection.Options.ShutdownTimeout).ConfigureAwait(false);
        }
    }
    private void CheckCapacity()
    {
        // 预占名额包含正在执行的 new/load/resume，避免并发请求突破上限。
        if (Interlocked.Increment(ref _sessionCount) <= Connection.Options.MaxSessions)
        {
            return;
        }

        Interlocked.Decrement(ref _sessionCount);
        throw new AcpRpcException(10002, "MaxSessions exceeded.");
    }
    private void Add(string id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryAdd(id, new()))
        {
            throw new ArgumentException("Duplicate sessionId.");
        }
    }
    private SessionRun Get(string id)
    {
        Ready();
        return _sessions.TryGetValue(id, out SessionRun? run) ? run : throw new AcpRpcException(-32002, "Session not found.");
    }
    private void Ready()
    {
        if (_context is null)
        {
            throw new AcpRpcException(-32600, "Initialize first.");
        }
    }
    public Task RunAsync(CancellationToken ct = default) => Connection.Completion.WaitAsync(ct);
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
    private sealed class SessionRun
    {
        internal int Busy; internal TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
