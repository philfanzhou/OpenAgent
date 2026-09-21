using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Errors;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class AgentExceptionHandlerMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_StreamEndpointThrowsBeforeResponseStarts_WritesProblemDetails()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/chat/stream";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"status\":500", payload);
        Assert.Contains("internal-error", payload);
    }

    [Fact]
    public async Task InvokeAsync_StreamEndpointRequestAborted_DoesNotWriteResponse()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/chat/stream";
        context.Response.Body = new MemoryStream();
        context.RequestAborted = aborted.Token;

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("event: error", payload);
        Assert.DoesNotContain("event: done", payload);
    }

    [Fact]
    public async Task InvokeAsync_EndpointThrows_WritesProblemDetailsWithUnifiedExtensions()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"status\":500", payload);
        Assert.Contains("internal-error", payload);
        Assert.Contains("\"traceId\"", payload);
        Assert.Contains("\"timestamp\"", payload);
    }

    [Fact]
    public async Task InvokeAsync_BadHttpRequestException_Returns400ProblemDetails()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/chat";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new BadHttpRequestException(
            "Failed to read parameter \"ChatRequest request\" from the request body as JSON."));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("invalid-request", payload);
        Assert.Contains("\"errorCode\":8001", payload);
        // instance 按 RFC 7807 语义携带请求路径，而非异常文本。
        Assert.Contains("\"instance\":\"/api/v1/agent/chat\"", payload);
    }

    [Fact]
    public async Task InvokeAsync_AgentException_InstanceCarriesRequestPathNotMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/files/missing";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new OpenAgent.Contracts.Security.AgentException(
            OpenAgent.Contracts.Requests.AgentErrorCode.NotFound, "File 'missing' was not found."));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"instance\":\"/api/v1/agent/files/missing\"", payload);
        // 详情文本只出现在 detail，不再复制进 instance。
        Assert.Equal(2, payload.Split("was not found").Length);
    }

    [Fact]
    public async Task InvokeAsync_InternalError_InstanceIsRequestPathWithoutExceptionText()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"instance\":\"/chat\"", payload);
        // 非 Development 环境不携带堆栈/异常类型等诊断文本。
        Assert.DoesNotContain("InvalidOperationException", payload);
        Assert.DoesNotContain("at ", payload);
    }

    [Fact]
    public async Task InvokeAsync_AgentException_ErrorBodyCarriesErrorCodeAndSymbolicCode()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new OpenAgent.Contracts.Security.AgentException(
            OpenAgent.Contracts.Requests.AgentErrorCode.TenantMismatch, "tenant does not match"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"errorCode\":5001", payload);
        Assert.Contains("tenant-mismatch", payload);
    }

    [Fact]
    public async Task InvokeAsync_ProviderRateLimit_Preserves429Status()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/chat/stream";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new HttpRequestException(
            "provider rate limit",
            inner: null,
            statusCode: HttpStatusCode.TooManyRequests));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("ProviderRateLimited", payload);
    }

    [Fact]
    public async Task InvokeAsync_ResponseAlreadyStarted_RethrowsException()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Request.Path = "/chat";

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_NoException_CallsNextAndPassesThrough()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static AgentExceptionHandlerMiddleware CreateMiddleware(RequestDelegate next)
    {
        var options = Options.Create(new AgentExceptionHandlingOptions());
        return new AgentExceptionHandlerMiddleware(
            next,
            NullLogger<AgentExceptionHandlerMiddleware>.Instance,
            new AgentExceptionMapper(options),
            options);
    }

    /// <summary>
    /// Simulates a response that has already started, forcing the rethrow branch.
    /// </summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
