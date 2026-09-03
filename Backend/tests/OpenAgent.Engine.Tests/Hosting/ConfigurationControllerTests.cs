using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Controllers;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class ConfigurationControllerTests
{
    [Fact]
    public async Task ModelRoutes_PreserveTenantIsolationSecretsAndContextTokensContract()
    {
        var profiles = new Dictionary<(string Tenant, string Id), LlmProviderProfile>();
        var repository = new Mock<ILlmConfigRepository>();
        repository.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, string id, CancellationToken _) => profiles.GetValueOrDefault((tenant, id)));
        repository.Setup(value => value.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LlmProviderProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, string id, LlmProviderProfile profile, CancellationToken _) => profiles[(tenant, id)] = profile);
        repository.Setup(value => value.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, CancellationToken _) => (IReadOnlyList<LlmProviderProfile>)profiles
                .Where(item => item.Key.Tenant == tenant).Select(item => item.Value).ToArray());
        repository.Setup(value => value.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, string id, CancellationToken _) => profiles.Remove((tenant, id)));
        var configuration = new ConfigurationService(
            new Mock<IAgentConfigRepository>().Object, repository.Object,
            new FakeRedisConnectionProvider(), Options.Create(new AgentConfigSourceOptions()),
            new ConfigurationSecretResolver(new ConfigurationBuilder().Build()),
            NullLogger<ConfigurationService>.Instance);
        using var provider = new RecordingModelHandler();
        using var providerClient = new HttpClient(provider);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(providerClient);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ConfigurationController).Assembly);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(clients.Object);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TenantAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        await using WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AgentUserContextMiddleware>();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using HttpResponseMessage anonymous = await client.GetAsync("/api/v1/admin/llm");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-a");
        var profile = new LlmProviderProfile
        {
            Id = "primary", Name = "Primary", ModelId = "model", Endpoint = "https://model.test/v1",
            ApiKey = "server-secret", ContextTokens = 8192, Modality = ModelModality.Multimodal
        };
        using HttpResponseMessage saved = await client.PutAsJsonAsync("/api/v1/admin/llm/primary", profile);
        saved.EnsureSuccessStatusCode();
        string payload = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("server-secret", payload, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(payload);
        Assert.Equal(8192, json.RootElement.GetProperty("contextTokens").GetInt32());
        profile.ApiKey = string.Empty;
        profile.ContextTokens = 16384;
        using HttpResponseMessage edited = await client.PutAsJsonAsync("/api/v1/admin/llm/primary", profile);
        edited.EnsureSuccessStatusCode();
        Assert.NotEqual("server-secret", profiles[("tenant-a", "primary")].ApiKey);
        Assert.Equal(16384, profiles[("tenant-a", "primary")].ContextTokens);
        using HttpResponseMessage tested = await client.PostAsJsonAsync(
            "/api/v1/admin/llm/test-connection", profile);
        tested.EnsureSuccessStatusCode();
        Assert.Equal("server-secret", provider.ApiKey);
        string listed = await client.GetStringAsync("/api/v1/admin/llm");
        Assert.DoesNotContain("server-secret", listed, StringComparison.Ordinal);

        client.DefaultRequestHeaders.Remove("X-Test-Tenant");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-b");
        using HttpResponseMessage foreign = await client.GetAsync("/api/v1/admin/llm/primary");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Tenant");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-a");
        using HttpResponseMessage deleted = await client.DeleteAsync("/api/v1/admin/llm/primary");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage removed = await client.GetAsync("/api/v1/admin/llm/primary");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        await app.StopAsync();
    }

    private sealed class RecordingModelHandler : HttpMessageHandler
    {
        internal string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TenantAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? tenant = Request.Headers["X-Test-Tenant"].FirstOrDefault();
            if (tenant == null) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", "user"), new Claim("tenant_id", tenant)], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
