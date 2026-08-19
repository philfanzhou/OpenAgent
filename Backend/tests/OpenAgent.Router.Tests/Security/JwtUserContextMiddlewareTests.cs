using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Security;
using Xunit;

namespace OpenAgent.Router.Tests.Security;

public class JwtUserContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ProductionHeaderWithoutTenantClaim_ReturnsBadRequest()
    {
        bool called = false;
        JwtUserContextMiddleware middleware = CreateMiddleware(
            Environments.Production,
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            });
        DefaultHttpContext context = CreateContext();
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task InvokeAsync_TenantClaimAndHeaderMismatch_ReturnsForbidden()
    {
        bool called = false;
        JwtUserContextMiddleware middleware = CreateMiddleware(
            Environments.Production,
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            });
        DefaultHttpContext context = CreateContext("trusted-tenant");
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task InvokeAsync_DevelopmentHeaderWithoutClaim_ReturnsBadRequest()
    {
        JwtUserContextMiddleware middleware = CreateMiddleware(
            Environments.Development,
            _ => Task.CompletedTask);
        DefaultHttpContext context = CreateContext();
        context.Request.Headers["X-Tenant-Id"] = "development-tenant";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static JwtUserContextMiddleware CreateMiddleware(
        string environmentName,
        RequestDelegate next)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(environmentName);
        return new JwtUserContextMiddleware(
            next,
            NullLogger<JwtUserContextMiddleware>.Instance,
            environment.Object);
    }

    private static DefaultHttpContext CreateContext(string? tenantId = null)
    {
        var claims = new List<Claim> { new("sub", "user-1") };
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/agent/chat";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }
}
