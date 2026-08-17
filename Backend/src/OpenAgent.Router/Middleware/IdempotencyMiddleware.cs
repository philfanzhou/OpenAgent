using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Middleware;

internal sealed class IdempotencyMiddleware(
    RequestDelegate next,
    ILogger<IdempotencyMiddleware> logger,
    RouterCacheSettings settings)
{
    private const int MaxIdempotencyKeyLength = 256;

    public async Task InvokeAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IIdempotencyStore store)
    {
        if (!userContext.IsAuthenticated || RouterCachePolicy.IsStreamingRequest(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string? clientKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (clientKey.Length > MaxIdempotencyKeyLength)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "The Idempotency-Key header exceeds 256 characters.").ConfigureAwait(false);
            return;
        }

        RequestBodySnapshot snapshot = await RequestBodySnapshot.GetAsync(
            context,
            settings.MaxRequestBodyBytes).ConfigureAwait(false);
        if (snapshot.IsOversized)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "The request body is too large for idempotent processing.").ConfigureAwait(false);
            return;
        }

        if (!snapshot.IsJson || snapshot.Digest.Length == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string? tenantId = context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString()
            ?? userContext.TenantId;
        string route = RouterCacheKeyFactory.GetRouteIdentity(context.Request);
        string requestDigest = RouterCacheKeyFactory.GetRequestDigest(context.Request, snapshot);
        string storageKey = RouterCacheKeyFactory.GetIdempotencyKey(
            tenantId ?? string.Empty,
            userContext.UserId,
            route,
            clientKey);
        string ownerToken = Guid.NewGuid().ToString("N");

        IdempotencyAcquireResult acquisition;
        try
        {
            acquisition = await store.AcquireAsync(
                storageKey,
                requestDigest,
                ownerToken,
                settings.IdempotencyPendingTimeToLive,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            RouterLog.IdempotencyCacheCheckFailed(
                logger,
                exception,
                TenantIsolationMiddleware.GetAction(context),
                clientKey,
                Activity.Current?.Id ?? context.TraceIdentifier);
            await next(context).ConfigureAwait(false);
            return;
        }

        if (acquisition.Status == IdempotencyAcquireStatus.Completed
            && acquisition.Response != null)
        {
            RouterLog.IdempotencyCacheHit(
                logger,
                TenantIsolationMiddleware.GetAction(context),
                clientKey,
                userContext.UserId,
                tenantId,
                Activity.Current?.Id ?? context.TraceIdentifier);
            await ReplayAsync(context, acquisition.Response).ConfigureAwait(false);
            return;
        }

        if (acquisition.Status is IdempotencyAcquireStatus.InProgress
            or IdempotencyAcquireStatus.RequestMismatch)
        {
            if (acquisition.Status == IdempotencyAcquireStatus.InProgress)
            {
                context.Response.Headers.RetryAfter = "1";
            }

            string detail = acquisition.Status == IdempotencyAcquireStatus.RequestMismatch
                ? "The Idempotency-Key was already used with a different request."
                : "A request with the same Idempotency-Key is already in progress.";
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                detail).ConfigureAwait(false);
            return;
        }

        Stream destination = context.Response.Body;
        var capture = new LimitedCaptureStream(destination, settings.MaxResponseBodyBytes);
        context.Response.Body = capture;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseSafelyAsync(store, storageKey, requestDigest, ownerToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            context.Response.Body = destination;
        }

        if (!RouterCachePolicy.IsSuccessful(context.Response.StatusCode)
            || RouterCachePolicy.IsStreamingContentType(context.Response.ContentType)
            || !capture.IsComplete)
        {
            await ReleaseSafelyAsync(store, storageKey, requestDigest, ownerToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await store.CompleteAsync(
                storageKey,
                requestDigest,
                ownerToken,
                new CachedResponse(
                    context.Response.StatusCode,
                    context.Response.ContentType,
                    capture.GetCapturedBody()),
                settings.IdempotencyTimeToLive,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
            {
                RouterLog.IdempotencyCacheCheckFailed(
                    logger,
                    exception,
                    TenantIsolationMiddleware.GetAction(context),
                    clientKey,
                    Activity.Current?.Id ?? context.TraceIdentifier);
            }

            await ReleaseSafelyAsync(store, storageKey, requestDigest, ownerToken).ConfigureAwait(false);
        }
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

    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string detail)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = "Idempotency conflict",
            status = statusCode,
            detail
        });
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ReleaseSafelyAsync(
        IIdempotencyStore store,
        string storageKey,
        string requestDigest,
        string ownerToken)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
        try
        {
            await store.ReleaseAsync(
                storageKey,
                requestDigest,
                ownerToken,
                cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RouterLog.IdempotencyReleaseFailed(logger, exception, storageKey);
        }
    }
}
