using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Engine.Hosting.Authentication;
using OpenAgent.Router.Tests.Integration;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public sealed class ConversationRecoveryProxyTests
{
    [Theory]
    [InlineData("GET", "stream")]
    [InlineData("POST", "cancel")]
    public async Task RecoveryRoutes_UseConversationAffinityAndForwardWithoutChatSelection(string method, string action)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication engine = builder.Build();
        string? forwardedTrace = null;
        string? forwardedConversation = null;
        engine.MapMethods("/api/v1/agent/conversations/{conversationId}/" + action, [method], (HttpContext context) =>
        {
            forwardedTrace = context.Request.Headers["X-Trace-Id"].FirstOrDefault();
            forwardedConversation = context.Request.RouteValues["conversationId"]?.ToString();
            return Results.NoContent();
        });
        await engine.StartAsync();
        RecordingRouteTable routes = new(engine.Urls.Single());
        using RouterApplicationFactory factory = new(engine.Urls.Single(), 100, 200);
        using var configuredFactory = factory.WithWebHostBuilder(host => host.ConfigureServices(services =>
        {
            services.RemoveAll<IRouteTable>();
            services.PostConfigure<AgentAuthenticationOptions>(options => options.BasicTenantId = "tenant-1");
            services.AddSingleton<IRouteTable>(routes);
        }));
        using HttpClient client = configuredFactory.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method),
            "/api/v1/agent/conversations/conversation-1/" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin")));
        request.Headers.Add("X-Trace-Id", "recovery-trace");
        request.Headers.Add("X-Conversation-Id", "wrong-header-conversation");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("chat", routes.Intent);
        Assert.Equal("tenant-1", routes.TenantId);
        Assert.Equal("conversation-1", routes.ConversationId);
        Assert.Equal("conversation-1", forwardedConversation);
        Assert.Equal("recovery-trace", forwardedTrace);
    }

    private sealed class RecordingRouteTable(string endpoint) : IRouteTable
    {
        internal string? Intent { get; private set; }
        internal string? TenantId { get; private set; }
        internal string? ConversationId { get; private set; }

        public string? GetTargetEndpoint(string intent) => endpoint;

        public string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
        {
            Intent = intent;
            TenantId = tenantId;
            ConversationId = conversationId;
            return endpoint;
        }
    }
}
