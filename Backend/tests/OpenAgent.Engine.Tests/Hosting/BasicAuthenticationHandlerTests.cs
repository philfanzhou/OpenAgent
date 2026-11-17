using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenAgent.Engine.Hosting;
using OpenAgent.Engine.Hosting.Authentication;
using OpenAgent.Engine.Hosting.Security;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class BasicAuthenticationHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_ValidCredential_EstablishesIdentity()
    {
        using ServiceProvider provider = CreateProvider(Environments.Development);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin"))}";

        AuthenticateResult result = await context.AuthenticateAsync(BasicAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Principal?.FindFirst("sub")?.Value);
        Assert.Equal("tenant-a", result.Principal?.FindFirst("tenant_id")?.Value);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidPassword_FailsAuthentication()
    {
        using ServiceProvider provider = CreateProvider(Environments.Development);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong-password"))}";

        AuthenticateResult result = await context.AuthenticateAsync(BasicAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_DevelopmentTenantHeader_DoesNotOverrideConfiguredClaim()
    {
        using ServiceProvider provider = CreateProvider(Environments.Development);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization =
            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin"))}";
        context.Request.Headers["X-Tenant-Id"] = "tenant-from-header";

        AuthenticateResult result = await context.AuthenticateAsync(
            BasicAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.Equal("tenant-a", result.Principal?.FindFirst("tenant_id")?.Value);
    }

    [Theory]
    [InlineData("Production", "admin", "admin", true)]
    [InlineData("Production", "test", "test", true)]
    [InlineData("Development", "admin", "wrong-password", false)]
    [InlineData("Production", "operator", "admin", false)]
    public async Task AuthenticateAsync_BuiltInAccounts_WorkInAnyEnvironment(
        string environment, string username, string password, bool succeeds)
    {
        using ServiceProvider provider = CreateProvider(environment);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization =
            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";

        AuthenticateResult result = await context.AuthenticateAsync(BasicAuthenticationHandler.SchemeName);

        Assert.Equal(succeeds, result.Succeeded);
        if (succeeds)
        {
            Assert.Equal(username, result.Principal?.FindFirst("sub")?.Value);
            Assert.Equal("tenant-a", result.Principal?.FindFirst("tenant_id")?.Value);
        }
    }

    [Theory]
    [InlineData("Production", "admin", "configured:password", true)]
    [InlineData("Production", "test", "configured:password", true)]
    [InlineData("Production", "admin", "admin", false)]
    [InlineData("Development", "admin", "admin", false)]
    [InlineData("Production", "operator", "configured:password", false)]
    public async Task AuthenticateAsync_ConfiguredPassword_OverridesBothBuiltInAccounts(
        string environment, string username, string password, bool succeeds)
    {
        using ServiceProvider provider = CreateProvider(environment, "configured:password");
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization =
            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";
        context.Request.Headers["X-Tenant-Id"] = "untrusted-tenant";

        AuthenticateResult result = await context.AuthenticateAsync(BasicAuthenticationHandler.SchemeName);

        Assert.Equal(succeeds, result.Succeeded);
        if (succeeds)
        {
            Assert.Equal(username, result.Principal?.FindFirst("sub")?.Value);
            Assert.Equal("tenant-a", result.Principal?.FindFirst("tenant_id")?.Value);
        }
    }

    [Fact]
    public async Task AuthenticateAsync_ProductionAnonymousFlag_DoesNotAuthenticate()
    {
        using ServiceProvider provider = CreateProvider(Environments.Production, "configured:password", true);
        var context = new DefaultHttpContext { RequestServices = provider };

        AuthenticateResult result = await context.AuthenticateAsync(BasicAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    private static ServiceProvider CreateProvider(
        string environmentName, string? password = null, bool allowAnonymous = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic",
                ["Authentication:AllowDevelopmentAnonymous"] = allowAnonymous.ToString(),
                ["Authentication:BasicPassword"] = password,
                ["Authentication:BasicTenantId"] = "tenant-a"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environmentName));
        services.AddAgentHost(configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        return services.BuildServiceProvider();
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(BasicAuthenticationHandlerTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
