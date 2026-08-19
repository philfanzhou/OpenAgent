using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Hosting;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class HostingTests
{
    [Fact]
    public void UseAgentHost_MapsLegacyHealthCheckAliases()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddHealthChecks()
            .AddCheck("live-check", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck("ready-check", () => HealthCheckResult.Healthy(), tags: new[] { "ready" });
        builder.Services.Configure<AgentHostOptions>(options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
        });

        var app = builder.Build();
        app.UseAgentHost(builder.Configuration);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/health", routePatterns);
        Assert.Contains("/ready", routePatterns);
        Assert.Contains("/health/live", routePatterns);
        Assert.Contains("/health/ready", routePatterns);
    }

    [Fact]
    public void UseAgentHost_MapsPrometheusMetricsEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAgentHost(builder.Configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = true;
        });

        var app = builder.Build();
        app.UseAgentHost(builder.Configuration);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/metrics", routePatterns);
    }

    [Fact]
    public void MapAgentEndpoints_PreservesBusinessRouteContract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();

        var app = builder.Build();
        app.MapAgentEndpoints();

        var routeEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        var expected = new[]
        {
            ("/api/v1/agent/chat", "POST", "Chat", "Agent"),
            ("/api/v1/agent/chat/stream", "POST", "ChatStream", "Agent"),
            ("/api/v1/agent/files", "POST", "UploadFileAsset", "File"),
            ("/api/v1/agent/files/{fileId}", "GET", "GetFileAsset", "File"),
            ("/api/v1/agent/files/{fileId}/content", "GET", "GetFileAssetContent", "File"),
            ("/api/v1/agent/files/{fileId}/download", "GET", "DownloadFileAsset", "File"),
            ("/api/v1/agent/agents", "GET", "ListAgents", "Agent"),
            ("/api/v1/agent/provider/conversations/{conversationId}", "GET", "ResolveProviderConversation", "Agent Provider"),
            ("/api/v1/agent/me", "GET", "CurrentAgentUser", "Agent"),
            ("/api/v1/agent/conversations", "GET", "ListConversations", "Conversation"),
            ("/api/v1/agent/conversations/search", "GET", "SearchConversations", "Conversation"),
            ("/api/v1/agent/conversations/{conversationId}", "GET", "GetConversation", "Conversation"),
            ("/api/v1/agent/conversations/{conversationId}", "DELETE", "DeleteConversation", "Conversation"),
            ("/api/v1/agent/conversations/{conversationId}/compact", "POST", "CompactConversation", "Conversation")
        };

        var actual = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/agent", StringComparison.Ordinal) == true)
            .Select(endpoint =>
            {
                IHttpMethodMetadata methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!;
                IEndpointNameMetadata name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!;
                ITagsMetadata tags = endpoint.Metadata.GetMetadata<ITagsMetadata>()!;
                return (
                    endpoint.RoutePattern.RawText!,
                    methods.HttpMethods.Single(),
                    name.EndpointName!,
                    tags.Tags.Single());
            })
            .OrderBy(route => route.Item1)
            .ToArray();

        Assert.Equal(expected.OrderBy(route => route.Item1), actual);
        Assert.All(routeEndpoints, endpoint =>
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.DoesNotContain(
            routeEndpoints,
            endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/admin",
                StringComparison.Ordinal) == true);
    }
}
