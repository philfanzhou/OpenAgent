using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host.Streaming;

internal sealed class ConversationRun
{
    private readonly object _gate = new();
    private readonly Func<AgentRequest, IAgentUserContext, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> _producer;
    private readonly Action<ConversationRun> _finalized;
    private readonly ActivityContext? _parentActivityContext;
    private readonly CancellationTokenSource _executionCancellation;
    private readonly List<AgentStreamEvent> _events = [];
    private readonly HashSet<Subscriber> _subscribers = [];
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private Exception? _failure;

    internal ConversationRun(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken hostShutdown,
        Func<AgentRequest, IAgentUserContext, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> producer,
        Action<ConversationRun> finalized)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(finalized);

        _parentActivityContext = Activity.Current?.Context;
        Request = CloneRequest(request);
        User = CloneUser(user);
        StartedAt = DateTimeOffset.UtcNow;
        _producer = producer;
        _finalized = finalized;
        _executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostShutdown);
    }

    internal AgentRequest Request { get; }

    internal IAgentUserContext User { get; }

    internal DateTimeOffset StartedAt { get; }

    internal Task Completion => _completion.Task;

    internal void Start()
    {
        try
        {
            using (ExecutionContext.SuppressFlow())
            {
                Task.Factory.StartNew(
                    static state => ((ConversationRun)state!).RunProducerAsync(),
                    this,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.HideScheduler,
                    TaskScheduler.Default).Unwrap();
            }
        }
        catch (Exception error)
        {
            Complete(error);
        }
    }

    internal void Cancel()
    {
        try
        {
            _executionCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The producer has already finalized; cancellation is idempotent for callers.
        }
    }

    internal async IAsyncEnumerable<AgentStreamEvent> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Subscriber? subscriber = null;
        AgentStreamEvent[] replay;
        bool completed;
        Exception? failure;

        lock (_gate)
        {
            replay = _events.ToArray();
            completed = _completed;
            failure = _failure;
            if (!completed)
            {
                subscriber = new Subscriber();
                _subscribers.Add(subscriber);
            }
        }

        try
        {
            foreach (AgentStreamEvent item in replay)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            if (completed)
            {
                ThrowFailure(failure);
                yield break;
            }

            await foreach (AgentStreamEvent item in subscriber!.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            Exception? finalFailure;
            lock (_gate)
            {
                finalFailure = _failure;
            }
            ThrowFailure(finalFailure);
        }
        finally
        {
            if (subscriber is not null)
            {
                lock (_gate)
                {
                    _subscribers.Remove(subscriber);
                }
            }
        }
    }

    private async Task RunProducerAsync()
    {
        // 无监听器（未开启追踪的部署）时为 null：退化为不建 span，行为与旧版一致。
        using Activity? producerActivity = StartProducerActivity();
        Exception? failure = null;
        try
        {
            await foreach (AgentStreamEvent item in _producer(
                Request,
                User,
                _executionCancellation.Token).WithCancellation(_executionCancellation.Token).ConfigureAwait(false))
            {
                Publish(item);
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            Complete(failure);
        }
    }

    /// <summary>
    /// 与宿主 OTLP 订阅（Engine.Hosting AddSource(OpenTelemetrySource)）同名：
    /// 生产者 span 必须可导出，轮次审计标签（AuditActivityEnrichment.Publish 写入
    /// Activity.Current）才能随 span 到达审计网关；裸 new Activity() 无监听器、
    /// 永不导出，会导致导出 span 丢失会话身份（audit 界面无法关联会话）。
    /// </summary>
    private static readonly ActivitySource ProducerSource = new("OpenAgent.Engine");

    private Activity? StartProducerActivity()
    {
        // 后台线程里 ExecutionContext 已被切断（Activity.Current 为 null），
        // 因此 parentContext 必须显式传入；为 null 时退化为独立 trace（同旧行为）。
        Activity? activity = _parentActivityContext is { } parent
            ? ProducerSource.StartActivity("OpenAgent.ConversationRun", ActivityKind.Internal, parent)
            : ProducerSource.StartActivity("OpenAgent.ConversationRun", ActivityKind.Internal);
        if (activity is not null && _parentActivityContext is { } context)
        {
            activity.TraceStateString = context.TraceState;
        }

        return activity;
    }

    private void Publish(AgentStreamEvent item)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _events.Add(item);
            foreach (Subscriber subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(item);
            }
        }
    }

    private void Complete(Exception? failure)
    {
        Subscriber[] subscribers;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _failure = failure;
            subscribers = _subscribers.ToArray();
        }

        foreach (Subscriber subscriber in subscribers)
        {
            subscriber.Writer.TryComplete();
        }

        try
        {
            _finalized(this);
        }
        finally
        {
            _completion.TrySetResult(true);
            _executionCancellation.Dispose();
        }
    }

    private static void ThrowFailure(Exception? failure)
    {
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static AgentRequest CloneRequest(AgentRequest request) => new()
    {
        Query = request.Query,
        AgentId = request.AgentId,
        LlmProfileId = request.LlmProfileId,
        RuntimeId = request.RuntimeId,
        ConversationId = request.ConversationId,
        ConversationType = request.ConversationType,
        TraceId = request.TraceId,
        ClientType = request.ClientType,
        IdempotencyKey = request.IdempotencyKey,
        ExternalContext = request.ExternalContext is null
            ? null
            : new Dictionary<string, string>(request.ExternalContext, StringComparer.Ordinal),
        FileIds = request.FileIds.ToArray()
    };

    private static AgentUserContext CloneUser(IAgentUserContext user) => new()
    {
        UserId = user.UserId,
        Username = user.Username,
        Email = user.Email,
        TenantId = user.TenantId,
        Groups = user.Groups.ToArray(),
        Roles = user.Roles.ToArray(),
        Claims = new Dictionary<string, string>(user.Claims, StringComparer.Ordinal),
        Audience = user.Audience.ToArray(),
        IsAuthenticated = user.IsAuthenticated
    };

    private sealed class Subscriber
    {
        private readonly Channel<AgentStreamEvent> _channel = Channel.CreateUnbounded<AgentStreamEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        internal ChannelReader<AgentStreamEvent> Reader => _channel.Reader;

        internal ChannelWriter<AgentStreamEvent> Writer => _channel.Writer;
    }
}
