using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Security;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router;

public static class RouterEndpointExtensions
{
    public static IEndpointRouteBuilder MapRouterEndpoints(this IEndpointRouteBuilder app)
    {
        var httpClient = new HttpMessageInvoker(HttpClientSecurity.CreateSocketsHttpHandler(
            app.ServiceProvider.GetRequiredService<IConfiguration>(),
            TimeSpan.FromSeconds(15)));
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
            .AddEndpointFilter<AgentSelectionFilter>()
            .WithName("RouterChat")
            .WithTags("Chat")
            .WithSummary("对话入口（转发到所选 Engine，支持 /stream 与 /sse 流式）");

        app.MapGet("/api/v1/agent/agents", (
            IAgentCatalogService catalog,
            IAgentUserContext userContext,
            HttpContext context,
            CancellationToken cancellationToken) =>
            AgentCatalogEndpointHandler.HandleAsync(
                catalog,
                userContext,
                context,
                cancellationToken))
            .WithName("RouterListAgents")
            .WithTags("Agent Catalog")
            .WithSummary("列出当前用户可见的 Agent");
        // Compatibility alias retained for clients that predate /api/v1/agent/agents;
        // hidden from OpenAPI docs to avoid a duplicate operation.
        app.MapGet("/api/v1/agents", (
            IAgentCatalogService catalog,
            IAgentUserContext userContext,
            HttpContext context,
            CancellationToken cancellationToken) =>
            AgentCatalogEndpointHandler.HandleAsync(
                catalog,
                userContext,
                context,
                cancellationToken))
            .ExcludeFromDescription();
        app.MapGet("/api/v1/agent/conversations", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, ILogger<Program> logger, int skip = 0, int take = 20) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, logger, httpClient, requestConfig,
                $"/api/v1/agent/conversations?skip={skip}&take={take}", conversationIdFromHeader: true))
            .WithName("RouterListConversations")
            .WithTags("Conversations")
            .WithSummary("列出会话（转发 Engine）");
        app.MapGet("/api/v1/agent/conversations/search", (
            HttpContext context, IHttpForwarder forwarder, IAgentUserContext userContext,
            IRouteTable routeTable, ILogger<Program> logger,
            string keyword = "", int skip = 0, int take = 20) =>
            GetEndpointHandler.HandleAsync(
                context, forwarder, userContext, routeTable, logger, httpClient, requestConfig,
                $"/api/v1/agent/conversations/search?keyword={Uri.EscapeDataString(keyword)}&skip={skip}&take={take}",
                conversationIdFromHeader: true))
            .WithName("RouterSearchConversations")
            .WithTags("Conversations")
            .WithSummary("搜索会话（转发 Engine）");
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
                    requireAuthentication: true))
            .WithName("RouterGetOrDeleteConversation")
            .WithTags("Conversations")
            .WithSummary("获取/删除会话（转发 Engine）");
        app.MapPost(
            "/api/v1/agent/conversations/{conversationId}/compact",
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
                    requireAuthentication: true))
            .WithName("RouterCompactConversation")
            .WithTags("Conversations")
            .WithSummary("压缩会话上下文（转发 Engine）");
        app.MapGet(
            "/api/v1/agent/conversations/{conversationId}/llm-interactions",
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
                    requireAuthentication: true))
            .WithName("RouterListLlmInteractions")
            .WithTags("Conversations")
            .WithSummary("列出会话 LLM 交互（转发 Engine）");
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
                requireAuthentication: true))
            .WithName("RouterCurrentUser")
            .WithTags("Agent Catalog")
            .WithSummary("当前认证用户信息（转发 Engine）");
        // File assets are owned by Engine, but clients use the Router as their
        // single API origin. Preserve multipart request bodies and binary
        // responses by forwarding every file method through YARP.
        app.MapMethods(
            "/api/v1/agent/files/{**path}",
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
                    requireAuthentication: true))
            .WithName("RouterFiles")
            .WithTags("Files")
            .WithSummary("文件资产上传/读取（透传 Engine，保留 multipart 与二进制载荷）");
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
                        requireAuthentication: true))
                .WithName("RouterAdminProxy")
                .WithTags("Admin")
                .WithSummary("开发环境管理端点代理（透传 Engine）");
        }
        return app;
    }
}
