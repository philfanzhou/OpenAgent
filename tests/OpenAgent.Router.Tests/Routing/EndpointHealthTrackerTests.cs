using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class EndpointHealthTrackerTests
{
    [Fact]
    public void IsAvailable_FailureThresholdAndCooldown_QuarantinesTemporarily()
    {
        MutableTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        EndpointHealthTracker tracker = new(
            2,
            TimeSpan.FromSeconds(30),
            timeProvider);

        tracker.ReportFailure("http://engine");
        Assert.True(tracker.IsAvailable("http://engine"));
        tracker.ReportFailure("http://engine");
        Assert.False(tracker.IsAvailable("http://engine"));

        timeProvider.Advance(TimeSpan.FromSeconds(31));

        Assert.True(tracker.IsAvailable("http://engine"));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }
}
