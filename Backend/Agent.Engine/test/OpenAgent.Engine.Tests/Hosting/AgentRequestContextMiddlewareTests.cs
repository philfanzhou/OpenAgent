using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Contracts.Requests;
using OpenAgent.Core.Exten;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentRequestContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_HeadersProvided_PopulatesContextAndScope()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "tenant-from-header";
        context.Request.Headers["X-Agent-Id"] = "agent-from-header";
        context.Request.Headers["X-Conversation-Id"] = "conv-from-header";
        context.Request.Headers["X-Trace-Id"] = "trace-from-header";

        var captureLogger = new CaptureLogger();
        Dictionary<string, object?>? capturedScope = null;
        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => { capturedScope = captureLogger.CurrentScope; return Task.CompletedTask; },
            captureLogger);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal("tenant-from-header", harness.Reader.TenantId);
        Assert.Equal("agent-from-header", harness.Reader.AgentId);
        Assert.Equal("conv-from-header", harness.Reader.ConversationId);
        Assert.Equal("trace-from-header", harness.Reader.TraceId);
        Assert.Equal("anonymous", harness.Reader.UserId);

        Assert.NotNull(capturedScope);
        Assert.Equal("tenant-from-header", capturedScope!["TenantId"]);
        Assert.Equal("agent-from-header", capturedScope["AgentId"]);
        Assert.Equal("conv-from-header", capturedScope["ConversationId"]);
        Assert.Equal("trace-from-header", capturedScope["TraceId"]);
        Assert.Equal("anonymous", capturedScope["UserId"]);
    }

    [Fact]
    public async Task InvokeAsync_TenantIdFromJwtClaim_TakesPriorityOverHeader()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim("tenant_id", "tenant-from-jwt"));
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-Id"] = "tenant-from-header";

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal("tenant-from-jwt", harness.Reader.TenantId);
        Assert.Equal("tenant-from-jwt", harness.Reader.UserContext.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_BodyContext_TakesPriorityOverHeaders()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Agent-Id"] = "agent-from-header";
        context.Request.Headers["X-Conversation-Id"] = "conv-from-header";
        context.Request.Headers["X-Trace-Id"] = "trace-from-header";

        var body = "{\"context\":{\"agentId\":\"agent-from-body\",\"conversationId\":\"conv-from-body\"}}";
        context.Request.Method = "POST";
        context.Request.Headers["Content-Type"] = "application/json";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.Headers["Content-Length"] = bodyBytes.Length.ToString();

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert: body context wins
        Assert.Equal("agent-from-body", harness.Reader.AgentId);
        Assert.Equal("conv-from-body", harness.Reader.ConversationId);
        // TraceId still comes from the header
        Assert.Equal("trace-from-header", harness.Reader.TraceId);
        // Body stream is reset for downstream consumers
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task InvokeAsync_JsonBodyWithoutContentLength_ReadsBodyContext()
    {
        // Arrange — chunked JSON requests (e.g. HttpClient.PostAsJsonAsync) carry no
        // Content-Length; the body pre-read must still apply.
        var context = new DefaultHttpContext();

        var body = "{\"context\":{\"agentId\":\"agent-from-body\",\"conversationId\":\"conv-from-body\"},\"message\":\"hello\"}";
        context.Request.Method = "POST";
        context.Request.Headers["Content-Type"] = "application/json; charset=utf-8";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal("agent-from-body", harness.Reader.AgentId);
        Assert.Equal("conv-from-body", harness.Reader.ConversationId);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task InvokeAsync_TopLevelAgentIdInBody_IsRespected()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Agent-Id"] = "agent-from-header";

        var body = "{\"agentId\":\"agent-top-level\",\"query\":\"hello\"}";
        context.Request.Method = "POST";
        context.Request.Headers["Content-Type"] = "application/json";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.Headers["Content-Length"] = bodyBytes.Length.ToString();

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal("agent-top-level", harness.Reader.AgentId);
    }

    [Fact]
    public async Task InvokeAsync_MissingValues_KeepsNullsAndAnonymousDefaults()
    {
        // Arrange — hard constraint: missing TenantId stays null, never a magic string
        var context = new DefaultHttpContext();

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Null(harness.Reader.TenantId);
        Assert.Null(harness.Reader.AgentId);
        Assert.Null(harness.Reader.ConversationId);
        Assert.Equal("anonymous", harness.Reader.UserId);
        Assert.Equal("anonymous", harness.Reader.UserContext.UserId);
        Assert.False(harness.Reader.UserContext.IsAuthenticated);
    }

    [Fact]
    public async Task InvokeAsync_BodyRead_DownstreamCanStillReadBody()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Trace-Id"] = "trace-1";

        var body = "{\"query\":\"hello\"}";
        context.Request.Method = "POST";
        context.Request.Headers["Content-Type"] = "application/json";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.Headers["Content-Length"] = bodyBytes.Length.ToString();

        string? bodyReadByNext = null;
        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            async _ =>
            {
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                bodyReadByNext = await reader.ReadToEndAsync();
            },
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert: downstream reads the full body
        Assert.Equal(body, bodyReadByNext);
    }

    [Fact]
    public async Task InvokeAsync_InvalidJsonBody_FallsBackToHeaders()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Agent-Id"] = "agent-from-header";

        var body = "{ this is not valid json";
        context.Request.Method = "POST";
        context.Request.Headers["Content-Type"] = "application/json";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.Headers["Content-Length"] = bodyBytes.Length.ToString();

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert: parse failure falls back to the header
        Assert.Equal("agent-from-header", harness.Reader.AgentId);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedUser_PopulatesRolesGroupsAndClaims()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Name, "user-1"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        identity.AddClaim(new Claim("roles", "operator"));
        identity.AddClaim(new Claim("groups", "team-a"));
        identity.AddClaim(new Claim("groups", "team-b"));
        identity.AddClaim(new Claim("custom", "custom-value"));
        context.User = new ClaimsPrincipal(identity);

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal("user-1", harness.Reader.UserId);
        Assert.True(harness.Reader.UserContext.IsAuthenticated);
        Assert.Equal(new[] { "admin", "operator" }, harness.Reader.UserContext.Roles);
        Assert.Equal(new[] { "team-a", "team-b" }, harness.Reader.UserContext.Groups);
        Assert.Equal("custom-value", harness.Reader.UserContext.Claims["custom"]);
        Assert.Equal("team-a,team-b", harness.Reader.UserContext.Claims["groups"]);
    }

    [Fact]
    public async Task InvokeAsync_AudienceHeader_PopulatesAudience()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Agent-Audience"] = "aud-1, aud-2,aud-1";

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal(new[] { "aud-1", "aud-2" }, harness.Reader.UserContext.Audience);
    }

    [Fact]
    public async Task InvokeAsync_AudienceItems_TakesPriorityOverHeader()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Items["Audience"] = new List<string> { "item-aud" };
        context.Request.Headers["X-Agent-Audience"] = "header-aud";

        using var harness = CreateHarness();
        var middleware = new AgentRequestContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentRequestContextMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, harness.Writer);

        // Assert
        Assert.Equal(new[] { "item-aud" }, harness.Reader.UserContext.Audience);
    }

    /// <summary>
    /// Builds a real DI scope through the production AddAgentCore registration so the
    /// test exercises the same triple mapping (concrete, reader, writer) as the host.
    /// </summary>
    private static RequestContextHarness CreateHarness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentCore(new ConfigurationBuilder().Build());
        ServiceProvider provider = services.BuildServiceProvider();
        IServiceScope scope = provider.CreateScope();
        return new RequestContextHarness(
            provider,
            scope,
            scope.ServiceProvider.GetRequiredService<IAgentRequestContextWriter>(),
            scope.ServiceProvider.GetRequiredService<IAgentRequestContext>());
    }

    private sealed class RequestContextHarness(
        ServiceProvider provider,
        IServiceScope scope,
        IAgentRequestContextWriter writer,
        IAgentRequestContext reader) : IDisposable
    {
        public IAgentRequestContextWriter Writer { get; } = writer;

        public IAgentRequestContext Reader { get; } = reader;

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    /// <summary>
    /// Simplified capture logger recording the last BeginScope dictionary state.
    /// </summary>
    private sealed class CaptureLogger : ILogger<AgentRequestContextMiddleware>
    {
        public Dictionary<string, object?>? CurrentScope { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IDictionary<string, object?> dict)
            {
                CurrentScope = new Dictionary<string, object?>(dict);
            }
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
