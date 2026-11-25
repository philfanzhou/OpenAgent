using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using OpenAgent.Engine.Hosting.Errors;
using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ForwardingContextBuilderTests
{
    [Fact]
    public async Task ApplyAsync_PreservesApplicationHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Agent-Id", "finance");
        request.Headers.Add("X-Tenant-Id", "tenant-1");
        request.Headers.Add("X-User-Id", "user-1");
        request.Headers.Add("X-Conversation-Id", "conversation-1");
        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            "trace-1");

        Assert.Equal("finance", Assert.Single(request.Headers.GetValues("X-Agent-Id")));
        Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Tenant-Id")));
        Assert.Equal("user-1", Assert.Single(request.Headers.GetValues("X-User-Id")));
        Assert.Equal("conversation-1", Assert.Single(request.Headers.GetValues("X-Conversation-Id")));
        Assert.Equal("trace-1", Assert.Single(request.Headers.GetValues("X-Trace-Id")));
    }

    [Fact]
    public async Task ApplyAsync_ReplacesInvalidTraceKeyAndPropagatesCurrentW3CContext()
    {
        using Activity activity = new Activity("router").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "openagent=fixture";
        activity.AddBaggage("request-origin", "local fixture");
        DefaultHttpContext context = new();
        context.Request.Headers["X-Trace-Id"] = new string('x', 65);
        string resolved = AgentProblemDetails.ResolveTraceId(context);
        using HttpRequestMessage request = new(HttpMethod.Post, "http://router/chat");
        request.Headers.TryAddWithoutValidation("X-Trace-Id", "bad/value");
        request.Headers.TryAddWithoutValidation("traceparent", "stale");

        await ForwardingContextBuilder.ApplyAsync(request, new Uri("http://engine/api/v1/agent/chat"), resolved);

        Assert.Equal(activity.TraceId.ToHexString(), Assert.Single(request.Headers.GetValues("X-Trace-Id")));
        Assert.Equal(activity.Id, Assert.Single(request.Headers.GetValues("traceparent")));
        Assert.Equal("openagent=fixture", Assert.Single(request.Headers.GetValues("tracestate")));
        Assert.Equal("request-origin=local%20fixture", Assert.Single(request.Headers.GetValues("baggage")));
    }
}
