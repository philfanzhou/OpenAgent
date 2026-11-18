using OpenAgent.Application.Execution;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host.Streaming;

internal sealed class ConversationRunRegistry : IHostedService, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<AgentRequest, IAgentUserContext, CancellationToken, IAsyncEnumerable<AgentStreamEvent>>? _producer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<RunKey, ConversationRun> _runs = [];
    private TaskCompletionSource<bool>? _stopCompletion;
    private bool _stopped;

    public ConversationRunRegistry(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    internal ConversationRunRegistry(
        Func<AgentRequest, IAgentUserContext, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> producer)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    }

    internal ConversationRun Start(AgentRequest request, IAgentUserContext user)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);

        string tenantId = user.TenantId ?? string.Empty;
        string conversationId = request.ConversationId ?? string.Empty;
        RunKey key = new(tenantId, conversationId);
        ConversationRun run;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_runs.ContainsKey(key))
            {
                throw new AgentException(
                    AgentErrorCode.Conflict,
                    "Conversation is already being processed by another request.");
            }

            run = new ConversationRun(
                request,
                user,
                _shutdown.Token,
                _producer ?? ExecuteWithScopeAsync,
                RemoveFinalizedRun);
            _runs.Add(key, run);
        }

        run.Start();
        return run;
    }

    internal ConversationRun? Find(string tenantId, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_gate)
        {
            return _runs.TryGetValue(new RunKey(tenantId, conversationId), out ConversationRun? run)
                ? run
                : null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        ConversationRun[] runs;
        bool initiateStop = false;

        lock (_gate)
        {
            _stopped = true;
            if (_stopCompletion is null)
            {
                _stopCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                runs = _runs.Values.ToArray();
                initiateStop = true;
            }
            else
            {
                runs = [];
            }

            completion = _stopCompletion;
        }

        if (initiateStop)
        {
            _ = StopRunsAsync(runs, completion);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task StopRunsAsync(
        IReadOnlyList<ConversationRun> runs,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            _shutdown.Cancel();
            foreach (ConversationRun run in runs)
            {
                run.Cancel();
            }

            await Task.WhenAll(runs.Select(run => run.Completion)).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private void RemoveFinalizedRun(ConversationRun run)
    {
        RunKey key = new(run.User.TenantId ?? string.Empty, run.Request.ConversationId ?? string.Empty);
        lock (_gate)
        {
            if (_runs.TryGetValue(key, out ConversationRun? candidate)
                && ReferenceEquals(candidate, run))
            {
                _runs.Remove(key);
            }
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> ExecuteWithScopeAsync(
        AgentRequest request,
        IAgentUserContext user,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUserContextInitializer>().Initialize(user);
        RuntimeAgentExecutor executor = scope.ServiceProvider.GetRequiredService<RuntimeAgentExecutor>();
        await foreach (AgentStreamEvent item in executor.ExecuteStreamingAsync(
            request,
            user,
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private readonly record struct RunKey(string TenantId, string ConversationId);
}
