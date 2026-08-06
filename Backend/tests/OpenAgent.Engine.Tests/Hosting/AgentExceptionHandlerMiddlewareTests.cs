using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentExceptionHandlerMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_EndpointThrows_WritesProblemDetails()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";
        context.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var payload = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"status\":500", payload);
        Assert.Contains("internal-error", payload);
    }

    [Fact]
    public async Task InvokeAsync_ResponseAlreadyStarted_RethrowsException()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Request.Path = "/chat";

        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_NoException_CallsNextAndPassesThrough()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Path = "/chat";

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static AgentExceptionHandlerMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new AgentExceptionHandlerMiddleware(
            next,
            NullLogger<AgentExceptionHandlerMiddleware>.Instance,
            new ErrorMapper(new ProblemDetailsFactory()));
    }

    /// <summary>
    /// Simulates a response that has already started, forcing the rethrow branch.
    /// </summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
