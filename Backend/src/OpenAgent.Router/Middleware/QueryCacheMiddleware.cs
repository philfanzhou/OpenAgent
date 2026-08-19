using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Middleware;

internal sealed class QueryCacheMiddleware(
    RequestDelegate next,
    ILogger<QueryCacheMiddleware> logger,
    RouterCacheSettings settings)
{
    internal const string QueryItemKey = "RouterQuery";

    public async Task InvokeAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IQueryCache queryCache)
    {
        if (!userContext.IsAuthenticated
            || RouterCachePolicy.IsStreamingRequest(context.Request)
            || !RequestBodySnapshot.IsJsonContentType(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        RequestBodySnapshot snapshot = await RequestBodySnapshot.GetAsync(
            context,
            settings.MaxRequestBodyBytes).ConfigureAwait(false);
        if (!RouterCachePolicy.IsQueryRequestCacheable(context.Request, snapshot))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Items[QueryItemKey] = ExtractQuery(snapshot.CanonicalBody);
        string? tenantId = userContext.TenantId;
        string route = RouterCacheKeyFactory.GetRouteIdentity(context.Request);
        string requestDigest = RouterCacheKeyFactory.GetRequestDigest(context.Request, snapshot);
        string cacheKey = RouterCacheKeyFactory.GetQueryKey(
            tenantId ?? string.Empty,
            userContext.UserId,
            route,
            requestDigest);

        try
        {
            CachedResponse? cached = await queryCache.GetAsync(
                cacheKey,
                context.RequestAborted).ConfigureAwait(false);
            if (cached != null)
            {
                RouterLog.QueryCacheHit(
                    logger,
                    RouterRequestMetadata.GetAction(context),
                    userContext.UserId,
                    tenantId,
                    context.Request.Headers["X-Conversation-Id"].FirstOrDefault(),
                    Activity.Current?.Id ?? context.TraceIdentifier);
                RouterMeter.RecordCacheOperation("query", "hit");
                await ReplayAsync(context, cached).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            RouterLog.QueryCacheFailed(logger, exception, route);
            await next(context).ConfigureAwait(false);
            return;
        }

        Stream destination = context.Response.Body;
        var capture = new LimitedCaptureStream(destination, settings.MaxResponseBodyBytes);
        context.Response.Body = capture;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = destination;
        }

        if (!capture.IsComplete || !RouterCachePolicy.IsQueryResponseCacheable(context.Response))
        {
            return;
        }

        try
        {
            await queryCache.SetAsync(
                cacheKey,
                new CachedResponse(
                    context.Response.StatusCode,
                    context.Response.ContentType,
                    capture.GetCapturedBody()),
                settings.QueryTimeToLive,
                context.RequestAborted).ConfigureAwait(false);
            RouterMeter.RecordCacheOperation("query", "write");
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
            {
                RouterLog.QueryCacheFailed(logger, exception, route);
            }
        }
    }

    internal static string ExtractQuery(byte[] canonicalBody)
    {
        using JsonDocument json = JsonDocument.Parse(canonicalBody);
        if (json.RootElement.TryGetProperty("query", out JsonElement query))
        {
            return query.GetString() ?? string.Empty;
        }

        return json.RootElement.TryGetProperty("message", out JsonElement message)
            ? message.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task ReplayAsync(HttpContext context, CachedResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(
            response.Body,
            context.RequestAborted).ConfigureAwait(false);
    }
}
