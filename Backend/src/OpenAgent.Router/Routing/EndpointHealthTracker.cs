using System.Collections.Concurrent;

namespace OpenAgent.Router.Routing;

internal sealed class EndpointHealthTracker : IEndpointHealthTracker
{
    private readonly ConcurrentDictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _failureThreshold;
    private readonly TimeSpan _quarantineDuration;
    private readonly TimeProvider _timeProvider;

    public EndpointHealthTracker(IConfiguration configuration)
        : this(
            Math.Max(configuration.GetValue("RouterSettings:ServiceDiscovery:FailureThreshold", 1), 1),
            TimeSpan.FromSeconds(Math.Max(
                configuration.GetValue("RouterSettings:ServiceDiscovery:FailureQuarantineSeconds", 30), 1)),
            TimeProvider.System)
    {
    }

    internal EndpointHealthTracker(
        int failureThreshold,
        TimeSpan quarantineDuration,
        TimeProvider timeProvider)
    {
        _failureThreshold = failureThreshold;
        _quarantineDuration = quarantineDuration;
        _timeProvider = timeProvider;
    }

    public bool IsAvailable(string endpoint)
    {
        if (!_failures.TryGetValue(endpoint, out FailureState? state))
        {
            return true;
        }

        if (state.Failures < _failureThreshold)
        {
            return true;
        }

        if (state.QuarantinedUntil <= _timeProvider.GetUtcNow())
        {
            _failures.TryRemove(endpoint, out _);
            return true;
        }

        return false;
    }

    public void ReportSuccess(string endpoint)
    {
        _failures.TryRemove(endpoint, out _);
    }

    public void ReportFailure(string endpoint)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _failures.AddOrUpdate(
            endpoint,
            _ => new FailureState(1, now + _quarantineDuration),
            (_, current) => new FailureState(current.Failures + 1, now + _quarantineDuration));
    }

    private sealed record FailureState(int Failures, DateTimeOffset QuarantinedUntil);
}
