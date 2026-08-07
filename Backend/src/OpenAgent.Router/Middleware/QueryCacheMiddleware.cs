using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Middleware;

internal sealed class QueryCacheMiddleware(RequestDelegate next, ILogger<QueryCacheMiddleware> logger)
{
    internal const string QueryItemKey = "RouterQuery";

    public async Task InvokeAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IQueryCache queryCache)
    {
        if (!userContext.IsAuthenticated)
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(CancellationToken.None);
        }

        context.Request.Body.Position = 0;
        var query = ExtractQuery(body);
        context.Items[QueryItemKey] = query;
        if (!string.IsNullOrEmpty(query))
        {
            var cached = await queryCache.GetCachedResponseAsync(query, context.RequestAborted);
            if (!string.IsNullOrEmpty(cached))
            {
                RouterLog.SemanticCacheHit(
                    logger, TenantIsolationMiddleware.GetAction(context), userContext.UserId,
                    context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString(),
                    context.Request.Headers["X-Conversation-Id"].FirstOrDefault(),
                    Activity.Current?.Id ?? context.TraceIdentifier);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(cached, context.RequestAborted);
                return;
            }
        }

        await next(context);
    }

    internal static string ExtractQuery(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("query", out var query))
            {
                return query.GetString() ?? string.Empty;
            }

            return json.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
