using Microsoft.AspNetCore.Http;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class StreamingResponseHeadersTests
{
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
