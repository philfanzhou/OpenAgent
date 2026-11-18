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
        using Activity producerActivity = StartProducerActivity();
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

    private Activity StartProducerActivity()
    {
        Activity activity = new("OpenAgent.ConversationRun");
        if (_parentActivityContext is { } parent)
        {
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            activity.TraceStateString = parent.TraceState;
        }

        activity.Start();
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
