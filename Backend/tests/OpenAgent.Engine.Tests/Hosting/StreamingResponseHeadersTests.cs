using Microsoft.AspNetCore.Http;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class StreamingResponseHeadersTests
{
    [Fact]
    public void ApplySse_SetsHeadersThatDisableProxyBuffering()
    {
        var context = new DefaultHttpContext();

        StreamingResponseHeaders.ApplySse(context);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache, no-transform", context.Response.Headers.CacheControl);
        Assert.Equal("keep-alive", context.Response.Headers.Connection);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public void ApplyNdjson_SetsHeadersThatDisableProxyBuffering()
    {
        var context = new DefaultHttpContext();

        StreamingResponseHeaders.ApplyNdjson(context);

        Assert.Equal("application/x-ndjson", context.Response.ContentType);
        Assert.Equal("no-cache, no-transform", context.Response.Headers.CacheControl);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
    }
}
