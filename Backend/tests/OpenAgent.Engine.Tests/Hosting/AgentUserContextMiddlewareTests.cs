using OpenAgent.Engine.Hosting;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Security;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentUserContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_MissingTenantOnAgentPath_ThrowsTenantIsolation()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext();

        await Assert.ThrowsAsync<TenantDataIsolationException>(() =>
            middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ClaimAndHeaderMismatch_ThrowsTenantMismatch()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("trusted-tenant");
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            middleware.InvokeAsync(context));

        Assert.Equal(AgentErrorCode.TenantMismatch, exception.ErrorCode);
    }

    [Fact]
    public async Task InvokeAsync_HeaderWithoutClaim_StillFailsTenantIsolation()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext();
        context.Request.Headers["X-Tenant-Id"] = "development-tenant";

        await Assert.ThrowsAsync<TenantDataIsolationException>(() =>
            middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedClaims_PopulatesIdentityProfile()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("trusted-tenant");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "keycloak-user-id"),
            new Claim("preferred_username", "alice"),
            new Claim("email", "alice@example.com"),
            new Claim("tid", "trusted-tenant")
        ], "Test"));

        await middleware.InvokeAsync(context);

        AgentRequestFeature request = context.Features.Get<AgentRequestFeature>()!;
        Assert.Equal("keycloak-user-id", request.User.UserId);
        Assert.Equal("alice", request.User.Username);
        Assert.Equal("alice@example.com", request.User.Email);
    }

    [Theory]
    [InlineData("subject-user", "mapped-user")]
    [InlineData("subject-user", null)]
    [InlineData(null, "mapped-user")]
    public async Task InvokeAsync_SubjectClaims_UsesSameUserAsConversationStore(string? subject, string? nameIdentifier)
    {
        DefaultHttpContext context = CreateContext("trusted-tenant");
        List<Claim> claims = [new("tid", "trusted-tenant")];
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }
        if (nameIdentifier is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        await CreateMiddleware().InvokeAsync(context);

        HttpCurrentUserContext currentUser = new(new HttpContextAccessor { HttpContext = context });
        Assert.Equal(subject ?? nameIdentifier, currentUser.UserId);
        Assert.Equal(currentUser.UserId, context.GetAgentRequest().User.UserId);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedWithoutSubject_DoesNotShareAnonymousConversations()
    {
        DefaultHttpContext context = CreateContext("trusted-tenant");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "trusted-tenant")], "Test"));
        HttpCurrentUserContext currentUser = new(new HttpContextAccessor { HttpContext = context });

        AgentException error = await Assert.ThrowsAsync<AgentException>(() => CreateMiddleware().InvokeAsync(context));

        Assert.Equal(AgentErrorCode.PermissionDenied, error.ErrorCode);
        Assert.Equal(string.Empty, currentUser.UserId);
        Assert.Null(context.Features.Get<AgentRequestFeature>());
    }

    private static AgentUserContextMiddleware CreateMiddleware()
    {
        return new AgentUserContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentUserContextMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(string? tenantId = null)
    {
        var claims = new List<Claim> { new("sub", "user-1") };
        if (tenantId != null)
        {
            claims.Add(new Claim("tid", tenantId));
        }

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/chat";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }
}
