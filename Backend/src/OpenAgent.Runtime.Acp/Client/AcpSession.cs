using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.Acp.Client;

/// <summary>独立会话，拥有一个更新消费者及至多一个进行中的 prompt。</summary>
public sealed class AcpSession : IAsyncDisposable
{
    private readonly AcpClient _client;
    private readonly AcpClient.SessionFeed _feed;
    private AcpPromptTurn? _active;
    private int _reading;
    private int _disposed;
    public string SessionId
    {
        get;
    }
    public SessionResponse State
    {
        get; private set;
    }
    internal AcpSession(AcpClient client, string id, SessionResponse state, AcpClient.SessionFeed feed)
    {
        _client = client;
        SessionId = id;
        State = state;
        _feed = feed;
    }
    /// <summary>持续消费会话更新，并在消费时同步配置状态；释放会话后仍可读完尾部队列。</summary>
    public async IAsyncEnumerable<SessionNotification> ReadUpdatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0)
        {
            throw new InvalidOperationException("Only one update consumer is allowed.");
        }

        try
        {
            await foreach (var item in _feed.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _client.Consumed(_feed, item.Bytes);
                if (item.Notification.Update is ConfigUpdate config)
                {
                    State = State with
                    {
                        ConfigOptions = config.ConfigOptions
                    };
                }

                if (item.Notification.Update is ModeUpdate mode && State.Modes is { } modes)
                {
                    State = State with
                    {
                        Modes = modes with
                        {
                            CurrentModeId = mode.CurrentModeId
                        }
                    };
                }

                yield return item.Notification;
            }
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }
    public Task<AcpPromptTurn> StartPromptAsync(ContentBlock[] content, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ct.ThrowIfCancellationRequested();
        PromptCapabilities? caps = _client.Negotiated!.AgentCapabilities?.PromptCapabilities;
        foreach (ContentBlock block in content)
        {
            AcpClient.Require(block switch
            {
                ImageContent => caps?.Image == true,
                AudioContent => caps?.Audio == true,
                EmbeddedResource => caps?.EmbeddedContext == true,
                _ => true
            });
        }

        AcpPromptTurn turn = new(this);
        lock (_feed)
        {
            if (_active is { Completion.IsCompleted: false })
            {
                throw new InvalidOperationException("Session is busy.");
            }

            _active = turn;
            turn.Start(_client.Connection.RequestAsync<PromptRequest, PromptResponse>("session/prompt", new(SessionId, content), timeout: Timeout.InfiniteTimeSpan), ct);
        }
        return Task.FromResult(turn);
    }
    public Task CancelAsync(CancellationToken ct = default) => _client.Connection.NotifyAsync("session/cancel", new SessionRequest(SessionId), ct);
    public async Task<Empty> SetModeAsync(string modeId, CancellationToken ct = default)
    {
        if (State.Modes?.AvailableModes.Any(x => x.Id == modeId) != true)
        {
            throw new ArgumentException("Unknown mode.");
        }

        Empty response = await _client.Connection.RequestAsync<SetModeRequest, Empty>("session/set_mode", new(SessionId, modeId), ct).ConfigureAwait(false);
        State = State with
        {
            Modes = State.Modes! with
            {
                CurrentModeId = modeId
            }
        };
        return response;
    }
    public async Task<SetConfigResponse> SetConfigAsync(string configId, JsonElement value, CancellationToken ct = default)
    {
        ConfigOption option = State.ConfigOptions?.FirstOrDefault(x => x.Id == configId) ?? throw new ArgumentException("Unknown config option.");
        if (option.Type == "boolean" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || option.Type == "select" && value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Invalid config value.");
        }

        if (option.Type == "select" && option.Options is { } choices && !ContainsOption(choices, value.GetString()))
        {
            throw new ArgumentException("Unknown config choice.");
        }

        SetConfigResponse response = await _client.Connection.RequestAsync<SetConfigRequest, SetConfigResponse>("session/set_config_option", new(SessionId, configId, value, option.Type == "boolean" ? "boolean" : null), ct).ConfigureAwait(false);
        State = State with
        {
            ConfigOptions = response.ConfigOptions
        };
        return response;
    }
    private static bool ContainsOption(JsonElement choices, string? id)
        => choices.EnumerateArray().Any(x => x.TryGetProperty("options", out JsonElement group) ? ContainsOption(group, id) : x.GetProperty("value").GetString() == id);
    internal TimeSpan CancelGrace => _client.Connection.Options.CancelGracePeriod;
    internal Task AbortAsync() => _client.Connection.DisposeAsync().AsTask();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_active is { Completion.IsCompleted: false })
            {
                await _active.CancelAndWaitAsync().ConfigureAwait(false);
            }

            if (_client.Negotiated?.AgentCapabilities?.SessionCapabilities?.Close is not null && !_client.Connection.Completion.IsCompleted)
            {
                await _client.Connection.RequestAsync<SessionRequest, Empty>("session/close", new(SessionId)).ConfigureAwait(false);
            }
        }
        finally
        {
            await _client.ReleaseAsync(SessionId).ConfigureAwait(false);
        }
    }
}

/// <summary>一次 prompt 的生命期。取消会等待 Agent 确认，超时则报告连接故障。</summary>
public sealed class AcpPromptTurn(AcpSession session) : IAsyncDisposable
{
    public Task<PromptResponse> Completion { get; private set; } = null!;
    internal void Start(Task<PromptResponse> completion, CancellationToken ct) => Completion = ObserveAsync(completion, ct);
    private async Task<PromptResponse> ObserveAsync(Task<PromptResponse> completion, CancellationToken ct)
    {
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = ct.Register(() => cancelled.TrySetResult());
        if (await Task.WhenAny(completion, cancelled.Task).ConfigureAwait(false) == completion)
        {
            return await completion.ConfigureAwait(false);
        }

        return await CancelCoreAsync(completion, CancellationToken.None).ConfigureAwait(false);
    }
    public async Task CancelAndWaitAsync(CancellationToken ct = default) => _ = await CancelCoreAsync(Completion, ct).ConfigureAwait(false);
    private async Task<PromptResponse> CancelCoreAsync(Task<PromptResponse> completion, CancellationToken ct)
    {
        // session/cancel 没有响应，必须等待原 prompt 的最终结果，不能发送后就宣称已取消。
        using CancellationTokenSource grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        grace.CancelAfter(session.CancelGrace);
        try
        {
            await session.CancelAsync(grace.Token).ConfigureAwait(false);
            return await completion.WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await session.AbortAsync().ConfigureAwait(false);
            throw new AcpCancellationUnconfirmedException();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!Completion.IsCompleted)
        {
            await CancelAndWaitAsync().ConfigureAwait(false);
        }
    }
}
public sealed class AcpCancellationUnconfirmedException() : IOException("Agent did not confirm cancellation.");
