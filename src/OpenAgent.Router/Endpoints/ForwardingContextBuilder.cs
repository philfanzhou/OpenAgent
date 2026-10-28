namespace OpenAgent.Router.Endpoints;

internal static class ForwardingContextBuilder
{
    internal static ValueTask ApplyAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        proxyRequest.RequestUri = targetUri;
        if (!proxyRequest.Headers.Contains("X-Trace-Id"))
        {
            proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        }
        return ValueTask.CompletedTask;
    }
}
