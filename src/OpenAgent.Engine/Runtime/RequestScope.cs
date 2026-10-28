namespace OpenAgent.Engine.Runtime;

internal class RequestScope : IDisposable
{
    private readonly ShutdownService _service;
    private readonly string _requestId;
    private bool _disposed;

    public RequestScope(ShutdownService service, string requestType, string? traceId = null)
    {
        _service = service;
        _requestId = service.RegisterRequest(requestType, traceId);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _service.CompleteRequest(_requestId);
            _disposed = true;
        }
    }
}
