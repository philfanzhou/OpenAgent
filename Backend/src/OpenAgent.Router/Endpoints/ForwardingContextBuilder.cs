using System.Diagnostics;
using OpenAgent.Engine.Hosting;

namespace OpenAgent.Router.Endpoints;

internal static class ForwardingContextBuilder
{
    internal static HttpMessageInvoker CreateClient(IConfiguration configuration)
    {
        SocketsHttpHandler handler = HttpClientSecurity.CreateSocketsHttpHandler(configuration, TimeSpan.FromSeconds(15));
        handler.ActivityHeadersPropagator = null;
        return new HttpMessageInvoker(handler);
    }

    internal static ValueTask ApplyAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId,
        bool propagateBaggage = true)
    {
        proxyRequest.RequestUri = targetUri;
        proxyRequest.Headers.Remove("X-Trace-Id");
        proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        foreach (string header in new[] { "traceparent", "tracestate", "baggage", "Correlation-Context" })
            proxyRequest.Headers.Remove(header);
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent } activity)
        {
            proxyRequest.Headers.TryAddWithoutValidation("traceparent", traceParent);
            if (!string.IsNullOrEmpty(activity.TraceStateString))
                proxyRequest.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
            if (propagateBaggage)
            {
                string baggage = string.Join(',', activity.Baggage
                    .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
                if (baggage.Length is > 0 and <= 8192)
                    proxyRequest.Headers.TryAddWithoutValidation("baggage", baggage);
            }
        }
        return ValueTask.CompletedTask;
    }
}
