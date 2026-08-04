using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace OpenAgent.Engine.Host.Extensions;

internal static class StreamingResponseHeaders
{
    public static void ApplySse(HttpContext context)
    {
        ApplyCommon(context);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers[HeaderNames.Connection] = "keep-alive";
    }

    public static void ApplyNdjson(HttpContext context)
    {
        ApplyCommon(context);
        context.Response.ContentType = "application/x-ndjson";
    }

    private static void ApplyCommon(HttpContext context)
    {
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }
}
