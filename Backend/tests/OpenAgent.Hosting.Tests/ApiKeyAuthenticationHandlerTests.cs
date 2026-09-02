using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenAgent.Contracts.Security;
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
        context.Request.Headers.Authorization = $"Bearer {ApiKey}";

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.Equal("integration:partner-a", result.Principal?.FindFirst("sub")?.Value);
        Assert.Equal("tenant-a", result.Principal?.FindFirst("tenant_id")?.Value);
        Assert.Equal("tenant-a", result.Principal?.FindFirst("tid")?.Value);
        Assert.Equal("agent.read conversation.write", result.Principal?.FindFirst("scope")?.Value);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, result.Ticket?.AuthenticationScheme);
    }

    [Fact]
    public async Task AuthenticateAsync_NonBearerApiKey_IsIgnored()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers["X-API-Key"] = ApiKey;

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidApiKey_FailsAuthentication()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = "Bearer oa_live_wrong";

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_EmptyBearerToken_FailsAuthentication()
    {
        using ServiceProvider provider = CreateProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = "Bearer ";

        AuthenticateResult result = await context.AuthenticateAsync(
            ApiKeyAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:EnableApiKey"] = "true"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddSingleton<IThirdPartyApiKeyIdentityResolver, FakeIdentityResolver>();
        services.AddAgentHost(configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        return services.BuildServiceProvider();
    }

    private sealed class FakeIdentityResolver : IThirdPartyApiKeyIdentityResolver
    {
        public Task<ThirdPartyApiKeyIdentity?> ResolveAsync(
            string apiKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ThirdPartyApiKeyIdentity?>(
                apiKey == ApiKey
                    ? new ThirdPartyApiKeyIdentity
                    {
                        UserId = "integration:partner-a",
                        Username = "partner-a",
                        TenantId = "tenant-a",
                        Claims = new Dictionary<string, string>
                        {
                            ["scope"] = "agent.read conversation.write"
                        },
                        Audience = ["openagent-api"]
                    }
                    : null);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(ApiKeyAuthenticationHandlerTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
