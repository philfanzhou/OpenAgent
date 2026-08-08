using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
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
    private readonly IGatewayAuthorizationService _authorization;
    private readonly HttpMessageInvoker _httpClient;

    public ExternalAgentForwarder(
        IHttpForwarder forwarder,
        IExternalAgentRegistry registry,
        IGatewayAuthorizationService authorization,
        IEnumerable<IExternalAgentAdapter> adapters)
    {
        _forwarder = forwarder;
        _registry = registry;
        _authorization = authorization;
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
        string? gatewayGrant = agent.ForwardGatewayGrant
            ? _authorization.IssueRestrictedGrant(
                userContext,
                BuildExternalPermissions(agent, userContext),
                agent.GatewayAudience ?? $"external:{agent.AgentId}")
            : null;
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
                traceId,
                gatewayGrant)).ConfigureAwait(false);
        return new ExternalForwardingResult(
            error,
            agent.BaseUrl.TrimEnd('/'),
            targetUri.ToString());
    }

    internal IReadOnlyList<string> BuildExternalPermissions(
        ExternalAgentOptions agent,
        IAgentUserContext userContext)
    {
        IReadOnlySet<string> granted = _authorization.ResolvePermissions(userContext);
        List<string> delegated =
        [
            $"{GatewayPermissions.AgentExecute}:{agent.RemoteAgentId ?? agent.AgentId}"
        ];
        foreach (string permission in new[]
        {
            GatewayPermissions.ModelInvoke,
            GatewayPermissions.ToolUse,
            GatewayPermissions.FunctionInvoke,
            GatewayPermissions.McpUse,
            GatewayPermissions.SkillUse
        })
        {
            if (GatewayPermissionMatcher.IsAllowed(granted, permission))
            {
                delegated.Add(permission);
            }

            delegated.AddRange(granted.Where(item => item.StartsWith(
                $"{permission}:",
                StringComparison.OrdinalIgnoreCase)));
        }

        return delegated.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose() => _httpClient.Dispose();
}
