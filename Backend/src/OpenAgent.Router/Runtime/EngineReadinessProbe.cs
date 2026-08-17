using System.Diagnostics;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router;

internal sealed class EngineReadinessProbe : IEngineReadinessProbe, IDisposable
{
    private readonly HttpMessageInvoker _client;
    private readonly ILogger<EngineReadinessProbe> _logger;
    private readonly string _path;
    private readonly TimeSpan _timeout;

    public EngineReadinessProbe(
        IConfiguration configuration,
        ILogger<EngineReadinessProbe> logger)
    {
        _logger = logger;
        _path = NormalizePath(configuration[
            "RouterSettings:ServiceDiscovery:ReadinessPath"] ?? "/ready");
        _timeout = TimeSpan.FromMilliseconds(Math.Max(configuration.GetValue(
            "RouterSettings:ServiceDiscovery:ReadinessTimeoutMs", 2000), 100));
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ActivityHeadersPropagator = DistributedContextPropagator.Current,
            ConnectTimeout = _timeout
        });
    }

    public async Task<bool> IsReadyAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"{endpoint.TrimEnd('/')}{_path}");
            using HttpResponseMessage response = await _client.SendAsync(
                request, timeout.Token).ConfigureAwait(false);
            bool isReady = response.IsSuccessStatusCode;
            RouterMeter.RecordDownstreamProbe(isReady ? "ready" : "not_ready");
            if (!isReady)
            {
                RouterLog.DownstreamNotReady(_logger, endpoint, (int)response.StatusCode);
            }

            return isReady;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RouterMeter.RecordDownstreamProbe("error");
            RouterLog.DownstreamProbeFailed(_logger, ex, endpoint);
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static string NormalizePath(string path) =>
        path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
}
