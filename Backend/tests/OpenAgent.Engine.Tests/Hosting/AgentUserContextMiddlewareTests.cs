using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentUserContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_MissingTenantOnAgentPath_ThrowsTenantIsolation()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware(Environments.Production);
        DefaultHttpContext context = CreateContext();

        await Assert.ThrowsAsync<TenantDataIsolationException>(() =>
            middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ClaimAndHeaderMismatch_ThrowsTenantMismatch()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware(Environments.Production);
        DefaultHttpContext context = CreateContext("trusted-tenant");
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            middleware.InvokeAsync(context));

        Assert.Equal(AgentErrorCode.TenantMismatch, exception.ErrorCode);
    }

    [Fact]
    public async Task InvokeAsync_DevelopmentHeaderWithoutClaim_StillFailsTenantIsolation()
    {
        AgentUserContextMiddleware middleware = CreateMiddleware(Environments.Development);
        DefaultHttpContext context = CreateContext();
        context.Request.Headers["X-Tenant-Id"] = "development-tenant";

        await Assert.ThrowsAsync<TenantDataIsolationException>(() =>
            middleware.InvokeAsync(context));
    }

    private static AgentUserContextMiddleware CreateMiddleware(string environmentName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(environmentName);
        return new AgentUserContextMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AgentUserContextMiddleware>.Instance,
            environment.Object,
            Options.Create(new OpenAgent.Hosting.Authentication.AgentAuthenticationOptions()));
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
