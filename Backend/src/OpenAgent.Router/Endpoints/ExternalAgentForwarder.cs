using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal sealed class ExternalAgentForwarder : IExternalAgentForwarder, IDisposable
{
    private static readonly ForwarderRequestConfig DefaultRequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromSeconds(100)
    };

    private static readonly ForwarderRequestConfig StreamingRequestConfig = new()
    {
        ActivityTimeout = Timeout.InfiniteTimeSpan
    };

    private readonly IHttpForwarder _forwarder;
    private readonly IExternalAgentRegistry _registry;
    private readonly IReadOnlyDictionary<string, IExternalAgentAdapter> _adapters;
    private readonly HttpMessageInvoker _httpClient;

    public ExternalAgentForwarder(
        IHttpForwarder forwarder,
        IExternalAgentRegistry registry,
        IEnumerable<IExternalAgentAdapter> adapters)
    {
        _forwarder = forwarder;
        _registry = registry;
        _adapters = adapters.ToDictionary(
            adapter => adapter.Name,
            StringComparer.OrdinalIgnoreCase);
        _httpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = DistributedContextPropagator.Current,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
    }

    public async Task<ExternalForwardingResult?> ForwardAsync(
        HttpContext context,
        string agentId,
        string? action,
        IAgentUserContext userContext,
        string? tenantId,
        string? conversationId,
        string traceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryGet(agentId, out ExternalAgentOptions? agent)
            || agent == null
            || !_adapters.TryGetValue(agent.Adapter, out IExternalAgentAdapter? adapter))
        {
            return null;
        }

        Uri targetUri = adapter.BuildTargetUri(agent, action);
        ForwarderRequestConfig requestConfig = action is "sse" or "stream" or "attachments/stream"
            ? StreamingRequestConfig
            : DefaultRequestConfig;
        ForwarderError error = await _forwarder.SendAsync(
            context,
            agent.BaseUrl.TrimEnd('/'),
            _httpClient,
            requestConfig,
            (_, proxyRequest) => adapter.ApplyAsync(
                proxyRequest,
                targetUri,
                agent,
                userContext,
                tenantId,
                conversationId,
                traceId)).ConfigureAwait(false);
        return new ExternalForwardingResult(
            error,
            agent.BaseUrl.TrimEnd('/'),
            targetUri.ToString());
    }

    public void Dispose() => _httpClient.Dispose();
}
