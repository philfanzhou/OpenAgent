using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAgent.Hosting.Authentication;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class AuthenticationEndpointTests
{
    [Fact]
    public async Task DevelopmentBasicEndpoints_ExposeWarningConfigurationAndCompatibilityLogin()
    {
        await using WebApplication app = await StartApplicationAsync(
            Environments.Development,
            new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic",
                ["Authentication:AllowDevelopmentAnonymous"] = "false"
            });
        using HttpClient client = CreateClient(app);

        JsonDocument config = await client.GetFromJsonAsync<JsonDocument>("/api/v1/auth/config")
            ?? throw new InvalidOperationException("Authentication config was empty.");
        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/v1/auth/password/token",
            new { username = "admin", password = "admin" });

        Assert.Equal("Basic", config.RootElement.GetProperty("mode").GetString());
        Assert.True(config.RootElement.GetProperty("development").GetBoolean());
        Assert.True(config.RootElement.GetProperty("password").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task ProductionJwtEndpoints_DisableCompatibilityPasswordLogin()
    {
        await using WebApplication app = await StartApplicationAsync(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "JwtBearer",
                ["Authentication:Authority"] = "https://identity.example",
                ["Authentication:Audience"] = "openagent-api",
                ["Authentication:ClientId"] = "openagent-chat"
            });
        using HttpClient client = CreateClient(app);

        JsonDocument config = await client.GetFromJsonAsync<JsonDocument>("/api/v1/auth/config")
            ?? throw new InvalidOperationException("Authentication config was empty.");
        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/v1/auth/password/token",
            new { username = "developer", password = "anything" });

        Assert.Equal("JwtBearer", config.RootElement.GetProperty("mode").GetString());
        Assert.False(config.RootElement.GetProperty("development").GetBoolean());
        Assert.False(config.RootElement.GetProperty("password").GetProperty("enabled").GetBoolean());
        Assert.Equal("openagent-chat", config.RootElement.GetProperty("oidc").GetProperty("clientId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
    }

    private static async Task<WebApplication> StartApplicationAsync(
        string environment,
        IReadOnlyDictionary<string, string?> settings)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAgentHost(builder.Configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentAuthenticationEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Server address was unavailable.");
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
