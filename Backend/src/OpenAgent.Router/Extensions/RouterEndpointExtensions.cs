using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Authentication;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Hosting.Authorization;
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
            IHttpForwarder forwarder,
            IExternalAgentForwarder externalForwarder,
            IAgentUserContext userContext,
            IGatewayAuthorizationService authorization,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
            ChatEndpointHandler.HandleAsync(
                action, context, forwarder, externalForwarder, userContext, authorization,
                logger, httpClient, requestConfig, cancellationToken))
            .AddEndpointFilter<AgentSelectionFilter>()
            .RequireAuthorization(GatewayPermissions.AgentExecute);

        app.MapGet("/api/v1/agent/agents", (
            HttpContext context,
            IAgentUserContext userContext,
            IRouteTable routeTable,
            IAgentCatalog catalog,
            IGatewayAuthorizationService authorization,
            CancellationToken cancellationToken) =>
            AgentCatalogEndpointHandler.HandleAsync(
                context,
                userContext,
                routeTable,
                catalog,
                authorization,
                cancellationToken))
            .RequireAuthorization(GatewayPermissions.AgentRead);
        // Compatibility alias retained for clients that predate /api/v1/agent/agents.
        app.MapGet("/api/v1/agents", (
            HttpContext context,
            IAgentUserContext userContext,
            IRouteTable routeTable,
            IAgentCatalog catalog,
            IGatewayAuthorizationService authorization,
            CancellationToken cancellationToken) =>
            AgentCatalogEndpointHandler.HandleAsync(
                context,
                userContext,
                routeTable,
                catalog,
                authorization,
                cancellationToken))
            .RequireAuthorization(GatewayPermissions.AgentRead);
        app.MapGet("/api/v1/agent/conversations", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, IGatewayAuthorizationService authorization,
            ILogger<Program> logger, int skip = 0, int take = 20) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, authorization, logger,
                httpClient, requestConfig,
                $"/api/v1/agent/conversations?skip={skip}&take={take}", conversationIdFromHeader: true))
            .RequireAuthorization(GatewayPermissions.ConversationRead);
        app.MapGet("/api/v1/agent/conversations/search", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, IGatewayAuthorizationService authorization,
            ILogger<Program> logger,
            string keyword = "", int skip = 0, int take = 20) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, authorization, logger,
                httpClient, requestConfig,
                $"/api/v1/agent/conversations/search?keyword={Uri.EscapeDataString(keyword)}&skip={skip}&take={take}",
                conversationIdFromHeader: true))
            .RequireAuthorization(GatewayPermissions.ConversationRead);
        app.MapMethods(
            "/api/v1/agent/conversations/{conversationId}",
            [HttpMethods.Get, HttpMethods.Delete],
            (
                HttpContext context,
                IHttpForwarder forwarder,
                IAgentUserContext userContext,
                IRouteTable routeTable,
                IGatewayAuthorizationService authorization,
                ILogger<Program> logger) =>
                GatewayProxyHandler.HandleAsync(
                    context,
                    forwarder,
                    userContext,
                    routeTable,
                    authorization,
                    logger,
                    httpClient,
                    requestConfig,
                    requireAuthentication: true))
            .RequireAuthorization();
        app.MapGet("/api/v1/agent/me", (
            HttpContext context,
            IHttpForwarder forwarder,
            IAgentUserContext userContext,
            IRouteTable routeTable,
            IGatewayAuthorizationService authorization,
            ILogger<Program> logger) =>
            GatewayProxyHandler.HandleAsync(
                context,
                forwarder,
                userContext,
                routeTable,
                authorization,
                logger,
                httpClient,
                requestConfig,
                requireAuthentication: true))
            .RequireAuthorization(GatewayPermissions.IdentityRead);
        app.MapMethods(
            "/api/v1/admin/{**path}",
            [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Patch],
            (
                HttpContext context,
                IHttpForwarder forwarder,
                IAgentUserContext userContext,
                IRouteTable routeTable,
                IGatewayAuthorizationService authorization,
                ILogger<Program> logger) =>
                GatewayProxyHandler.HandleAsync(
                    context,
                    forwarder,
                    userContext,
                    routeTable,
                    authorization,
                    logger,
                    httpClient,
                    requestConfig,
                    requireAuthentication: true))
            .RequireAuthorization();
        app.MapGet("/api/v1/auth/config", (
            IOptions<AgentAuthenticationOptions> options) =>
            GatewayAuthenticationEndpointHandler.GetConfig(options));
        app.MapPost("/api/v1/auth/password/token", (
            [FromBody] PasswordLoginRequest request,
            IOptions<AgentAuthenticationOptions> options,
            IHostEnvironment environment) =>
            GatewayAuthenticationEndpointHandler.CreateDevelopmentBasicCredential(
                request,
                options,
                environment));
        return app;
    }
}
