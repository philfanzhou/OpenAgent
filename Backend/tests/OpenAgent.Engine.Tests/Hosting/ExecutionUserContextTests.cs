using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Hosting;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ExecutionUserContextTests
{
    [Fact]
    public void OwnedScopes_KeepIndependentIdentityWithoutHttpContext()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAgentHost(new ConfigurationBuilder().Build(), options =>
        {
            options.EnableJwtAuth = false;
            options.EnableOpenTelemetry = false;
            options.EnableHealthChecks = false;
            options.EnableSwagger = false;
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();
        first.ServiceProvider.GetRequiredService<ICurrentUserContextInitializer>().Initialize(new AgentUserContext
        {
            UserId = "first-user", TenantId = "first-tenant", Roles = ["reviewer"]
        });
        second.ServiceProvider.GetRequiredService<ICurrentUserContextInitializer>().Initialize(new AgentUserContext
        {
            UserId = "second-user", TenantId = "second-tenant"
        });
        ICurrentUserContext firstUser = first.ServiceProvider.GetRequiredService<ICurrentUserContext>();
        ICurrentUserContext secondUser = second.ServiceProvider.GetRequiredService<ICurrentUserContext>();

        Assert.True(firstUser.IsAuthenticated);
        Assert.Equal("first-user", firstUser.UserId);
        Assert.Equal("first-tenant", firstUser.TenantId);
        Assert.True(firstUser.IsInRole("reviewer"));
        Assert.Equal("second-user", secondUser.UserId);
        Assert.Equal("second-tenant", secondUser.TenantId);
        Assert.False(secondUser.IsInRole("reviewer"));
        Assert.Null(provider.GetRequiredService<IHttpContextAccessor>().HttpContext);
    }

    [Fact]
    public void OwnedIdentity_DoesNotFollowLaterHttpRequestAndCannotBeReinitialized()
    {
        HttpContextAccessor accessor = new();
        OpenAgent.Engine.Hosting.Security.HttpCurrentUserContext current = new(accessor);
        current.Initialize(new AgentUserContext { UserId = "owner", TenantId = "owner-tenant" });
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "other"), new Claim("tid", "other-tenant")], "test"))
        };
        try
        {
            Assert.Equal("owner", current.UserId);
            Assert.Equal("owner-tenant", current.TenantId);
            Assert.Throws<InvalidOperationException>(() => current.Initialize(new AgentUserContext { UserId = "other" }));
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
