using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Hosting.Security;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class BasicAuthenticationHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_DevelopmentCredential_EstablishesIdentityWithoutPasswordValidation()
    {
        using ServiceProvider provider = CreateProvider(Environments.Development);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("developer:not-a-verified-password"))}";
        context.Request.Headers["X-Tenant-Id"] = "tenant-1";

        AuthenticateResult result = await context.AuthenticateAsync(BasicAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.Equal("developer", result.Principal?.FindFirst("sub")?.Value);
        Assert.Equal("tenant-1", result.Principal?.FindFirst("tenant_id")?.Value);
    }

    [Fact]
    public void AuthenticationOptions_ProductionBasicMode_IsRejected()
    {
        using ServiceProvider provider = CreateProvider(Environments.Production);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentAuthenticationOptions>>().Value);
        Assert.Contains("Development", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic",
                ["Authentication:AllowTenantHeader"] = "true",
                ["Authentication:AllowDevelopmentAnonymous"] = "false"
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
