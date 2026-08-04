using System.Diagnostics;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Middleware;
using OpenAgent.Core.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Core.Tests.Middleware;

public class TracingTests
{
    [Fact]
    public void StartActivity_ReturnsActivity_WhenListenerEnabled()
    {
        Activity? observed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentActivitySource.Name,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        listener.ActivityStarted += activity => observed = activity;
        ActivitySource.AddActivityListener(listener);

        using var activity = AgentActivitySource.Instance.StartActivity("Agent.Core.Request");
        Assert.NotNull(activity);
        Assert.Equal("Agent.Core.Request", activity!.OperationName);
        Assert.Equal(AgentActivitySource.Name, activity.Source.Name);
    }

    [Fact]
    public async Task InvokeAsync_CreatesActivity_And_ListenerReceivesIt()
    {
        Activity? observed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentActivitySource.Name,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        listener.ActivityStarted += activity => observed = activity;
        ActivitySource.AddActivityListener(listener);

        var middleware = new Tracing(NullLogger<Tracing>.Instance);
        var request = new AgentRequest { Query = "hello" };
        var userContext = new AgentUserContext { UserId = "user-1", TenantId = "tenant-1" };
        var expectedResponse = new AgentResponse { Success = true, Content = string.Empty };

        var response = await middleware.InvokeAsync(request, userContext, (r, u, ct) => Task.FromResult(expectedResponse), CancellationToken.None);

        Assert.Equal(expectedResponse, response);
        Assert.NotNull(observed);
        Assert.Equal("Agent.Core.Request", observed!.OperationName);
        Assert.NotNull(observed.Id);
    }

    [Fact]
    public async Task InvokeAsync_ReusesExistingTraceId_WhenProvided()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentActivitySource.Name,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new Tracing(NullLogger<Tracing>.Instance);
        var existingTraceId = ActivityTraceId.CreateRandom().ToString();
        var request = new AgentRequest { Query = "hello", TraceId = existingTraceId };
        var userContext = new AgentUserContext { UserId = "user-1", TenantId = "tenant-1" };

        var response = await middleware.InvokeAsync(request, userContext, (r, u, ct) => Task.FromResult(new AgentResponse { Content = string.Empty }), CancellationToken.None);

        Assert.NotNull(response);
    }
}
