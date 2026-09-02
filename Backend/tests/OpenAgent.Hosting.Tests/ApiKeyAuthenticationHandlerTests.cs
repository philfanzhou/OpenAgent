using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Hosting.Security;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    private const string ApiKey = "oa_live_test-key-that-is-never-committed";

    [Fact]
    public async Task AuthenticateAsync_ValidApiKey_EstablishesTrustedIdentity()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers["X-API-Key"] = ApiKey;

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.Equal("integration:partner-a", result.Principal?.FindFirst("sub")?.Value);
        Assert.Equal("partner-a", result.Principal?.FindFirst("client_id")?.Value);
        Assert.Equal("tenant-a", result.Principal?.FindFirst("tenant_id")?.Value);
        Assert.Equal("tenant-a", result.Principal?.FindFirst("tid")?.Value);
        Assert.Equal("agent.read conversation.write", result.Principal?.FindFirst("scope")?.Value);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, result.Ticket?.AuthenticationScheme);
    }

    [Fact]
    public async Task AuthenticateAsync_BearerApiKey_IsAccepted()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Bearer {ApiKey}";

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidApiKey_FailsAuthentication()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers["X-API-Key"] = "oa_live_wrong";

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_ConflictingHeaders_FailsAuthentication()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers["X-API-Key"] = ApiKey;
        context.Request.Headers.Authorization = "Bearer oa_live_other";

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "ApiKey",
                ["Authentication:ApiKeyHash"] = Hash(ApiKey),
                ["Authentication:ApiKeyTenantId"] = "tenant-a",
                ["Authentication:ApiKeyClientId"] = "partner-a",
                ["Authentication:ApiKeyScopes:0"] = "agent.read",
                ["Authentication:ApiKeyScopes:1"] = "conversation.write"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddAgentHost(configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        return services.BuildServiceProvider();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(ApiKeyAuthenticationHandlerTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
