using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Security;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router;

public static class RouterEndpointExtensions
{
    public static IEndpointRouteBuilder MapRouterEndpoints(this IEndpointRouteBuilder app)
    {
        var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = DistributedContextPropagator.Current,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
        var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

        app.MapPost("/api/v1/agent/chat/{*action}", (
            string? action,
            HttpContext context,
            IAgentProviderRegistry providers,
            IAgentForwarder agentForwarder,
            IAgentUserContext userContext,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
            ChatEndpointHandler.HandleAsync(
                action, context, providers, agentForwarder, userContext, logger, cancellationToken))
            .AddEndpointFilter<AgentSelectionFilter>();

        app.MapGet("/api/v1/agent/agents", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, ILogger<Program> logger) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, logger,
                httpClient, requestConfig, "/api/v1/agent/agents"));
        // Compatibility alias retained for clients that predate /api/v1/agent/agents.
        app.MapGet("/api/v1/agents", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, ILogger<Program> logger) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, logger,
                httpClient, requestConfig, "/api/v1/agent/agents"));
        app.MapGet("/api/v1/agent/conversations", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, ILogger<Program> logger, int skip = 0, int take = 20) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, logger, httpClient, requestConfig,
                $"/api/v1/agent/conversations?skip={skip}&take={take}", conversationIdFromHeader: true));
        app.MapGet("/api/v1/agent/conversations/search", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, ILogger<Program> logger,
            string keyword = "", int skip = 0, int take = 20) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, logger, httpClient, requestConfig,
                $"/api/v1/agent/conversations/search?keyword={Uri.EscapeDataString(keyword)}&skip={skip}&take={take}",
                conversationIdFromHeader: true));
        app.MapMethods(
            "/api/v1/agent/conversations/{conversationId}",
            [HttpMethods.Get, HttpMethods.Delete],
            (
                HttpContext context,
                IHttpForwarder forwarder,
                IAgentUserContext userContext,
                IRouteTable routeTable,
                ILogger<Program> logger) =>
                GatewayProxyHandler.HandleAsync(
                    context,
                    forwarder,
                    userContext,
                    routeTable,
                    logger,
                    httpClient,
                    requestConfig,
                    requireAuthentication: true));
        app.MapGet("/api/v1/agent/me", (
            HttpContext context,
            IHttpForwarder forwarder,
            IAgentUserContext userContext,
            IRouteTable routeTable,
            ILogger<Program> logger) =>
            GatewayProxyHandler.HandleAsync(
                context,
                forwarder,
                userContext,
                routeTable,
                logger,
                httpClient,
                requestConfig,
                requireAuthentication: true));
        IHostEnvironment environment = app.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment())
        {
            // Basic authentication is a local-development convenience and does not
            // establish a production authorization boundary. Keep both the login
            // endpoint and the management proxy unreachable outside Development.
            app.MapMethods(
                "/api/v1/admin/{**path}",
                [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Patch],
                (
                    HttpContext context,
                    IHttpForwarder forwarder,
                    IAgentUserContext userContext,
                    IRouteTable routeTable,
                    ILogger<Program> logger) =>
                    GatewayProxyHandler.HandleAsync(
                        context,
                        forwarder,
                        userContext,
                        routeTable,
                        logger,
                        httpClient,
                        requestConfig,
                        requireAuthentication: true));
            app.MapMethods(
                "/api/v1/auth/{**path}",
                [HttpMethods.Get, HttpMethods.Post],
                (
                    HttpContext context,
                    IHttpForwarder forwarder,
                    IAgentUserContext userContext,
                    IRouteTable routeTable,
                    ILogger<Program> logger) =>
                    GatewayProxyHandler.HandleAsync(
                        context,
                        forwarder,
                        userContext,
                        routeTable,
                        logger,
                        httpClient,
                        requestConfig,
                        requireAuthentication: false));
        }
        return app;
    }
}
