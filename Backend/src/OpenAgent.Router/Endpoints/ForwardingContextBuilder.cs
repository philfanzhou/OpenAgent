namespace OpenAgent.Router.Endpoints;

internal static class ForwardingContextBuilder
{
    internal static ValueTask ApplyAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string? conversationId,
        string traceId)
    {
        proxyRequest.RequestUri = targetUri;
        proxyRequest.Headers.Remove("X-Conversation-Id");
        proxyRequest.Headers.Remove("X-Trace-Id");
        proxyRequest.Headers.Remove("X-User-Id");
        proxyRequest.Headers.Remove("X-Tenant-Id");
        proxyRequest.Headers.Add("X-Trace-Id", traceId);
        if (!string.IsNullOrEmpty(conversationId)) proxyRequest.Headers.Add("X-Conversation-Id", conversationId);
        return ValueTask.CompletedTask;
    }
}
