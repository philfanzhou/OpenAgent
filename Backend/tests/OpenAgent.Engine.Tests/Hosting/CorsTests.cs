using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Hosting;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class CorsTests
{
    [Theory]
    [InlineData(null, "https://frontend.other.example")]
    [InlineData("*", "http://192.0.2.10:8090")]
    [InlineData("*", "https://another.example:9443")]
    public async Task UseAgentHost_AnyOrigin_AllowsPreflightAndPreservesAuthentication(
        string? configuredOrigin, string origin)
    {
        await using WebApplication app = await StartApplicationAsync(configuredOrigin);
        using HttpClient client = CreateClient(app);
        using HttpRequestMessage preflight = new(HttpMethod.Options, "/protected");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-tenant-id,x-trace-id");

        using HttpResponseMessage allowed = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal("*", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("POST", allowed.Headers.GetValues("Access-Control-Allow-Methods"));
        string headers = string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers"));
        foreach (string header in new[] { "authorization", "content-type", "x-tenant-id", "x-trace-id" })
        {
            Assert.Contains(header, headers, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));

        client.DefaultRequestHeaders.Add("Origin", origin);
        using HttpResponseMessage unauthenticated = await client.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("*", Assert.Single(unauthenticated.Headers.GetValues("Access-Control-Allow-Origin")));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin")));
        using HttpResponseMessage authenticated = await client.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal("*", Assert.Single(authenticated.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("X-OpenAgent-Selected-Agent-Id", authenticated.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.False(authenticated.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("https://trusted.example", true)]
    [InlineData("https://untrusted.example", false)]
    public async Task UseAgentHost_ExplicitOrigins_PreservesAllowlist(string origin, bool expected)
    {
        await using WebApplication app = await StartApplicationAsync("https://trusted.example");
        using HttpClient client = CreateClient(app);
        using HttpRequestMessage preflight = new(HttpMethod.Options, "/protected");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        using HttpResponseMessage response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(expected, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (expected)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        }
        else
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    private static async Task<WebApplication> StartApplicationAsync(string? configuredOrigin)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Basic",
            ["Authentication:AllowDevelopmentAnonymous"] = "false"
        });
        if (configuredOrigin is not null)
        {
            builder.Configuration["Cors:AllowedOrigins:0"] = configuredOrigin;
        }
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAgentHost(builder.Configuration, options =>
        {
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        WebApplication app = builder.Build();
        app.UseAgentHost(builder.Configuration);
        app.MapMethods("/protected", ["GET", "POST"], () => Results.Ok()).RequireAuthorization();
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
