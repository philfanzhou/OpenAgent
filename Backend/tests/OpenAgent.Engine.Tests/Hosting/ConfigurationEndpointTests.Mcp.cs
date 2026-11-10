using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public partial class ConfigurationEndpointTests
{
    private const string McpTestRoute = "/api/v1/admin/mcp/test-connection";
    private const string RemoteSecret = "remote-private-password-and-response";

    [Theory]
    [InlineData(McpServerType.Http)]
    [InlineData(McpServerType.SSE)]
    public async Task McpTest_ReadsEveryCatalogPageAndPreservesSchemasWithoutCallingTools(McpServerType type)
    {
        var cursors = new ConcurrentQueue<string?>();
        await using McpTestServer server = await McpTestServer.StartAsync((cursor, _) =>
        {
            cursors.Enqueue(cursor);
            return ValueTask.FromResult(cursor is null
                ? new ListToolsResult { Tools = [CatalogTool()], NextCursor = "second-page" }
                : new ListToolsResult { Tools = [new Tool { Name = "second", InputSchema = Schema("{\"type\":\"object\"}") }] });
        });
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        var config = new McpServerConfig
        {
            Name = "unsaved", Url = server.Url(type), Type = type, ProtocolVersion = "2024-11-05",
            TenantId = "must-not-override-request-tenant"
        };
        McpConnectionTestResult result = await admin.TestAsync(config);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Connected);
        Assert.Null(result.Error);
        Assert.Equal("Catalog test server", result.ServerName);
        Assert.Equal("2.3.4", result.ServerVersion);
        Assert.Equal("2024-11-05", result.ProtocolVersion);
        Assert.True(result.LatencyMs >= 0);
        Assert.Equal(new string?[] { null, "second-page" }, cursors.ToArray());
        Assert.Equal(["search", "second"], result.Tools.Select(tool => tool.Name));
        McpToolDefinition tool = result.Tools[0];
        Assert.Equal("Search title", tool.Title);
        Assert.Equal("Search the catalog", tool.Description);
        Assert.Equal("query", tool.InputSchema.GetProperty("required")[0].GetString());
        Assert.Equal("integer", tool.InputSchema.GetProperty("properties").GetProperty("options")
            .GetProperty("properties").GetProperty("limit").GetProperty("type").GetString());
        Assert.Equal("string", tool.OutputSchema!.Value.GetProperty("properties").GetProperty("items")
            .GetProperty("items").GetProperty("type").GetString());
        Assert.Null(result.Tools[1].OutputSchema);
        Assert.Equal("2024-11-05", Assert.Single(server.RequestedVersions));
        Assert.DoesNotContain("tools/call", server.Methods);
        Assert.DoesNotContain("resources/list", server.Methods);
        Assert.DoesNotContain("prompts/list", server.Methods);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpTest_EmptyCatalogAndAbsentToolsCapabilityAreSuccessful(bool advertiseTools)
    {
        await using McpTestServer server = await McpTestServer.StartAsync(advertiseTools
            ? (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [] })
            : null);
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig { Url = server.Url() });
        Assert.True(result.Success, result.Error);
        Assert.True(result.Connected);
        Assert.Empty(result.Tools);
        Assert.Equal(advertiseTools ? 1 : 0, server.Methods.Count(method => method == "tools/list"));
        Assert.DoesNotContain("tools/call", server.Methods);
    }

    [Fact]
    public async Task McpTest_ListFailureKeepsHandshakeMetadataButNeverReturnsPartialCatalogOrRemoteErrors()
    {
        await using McpTestServer server = await McpTestServer.StartAsync((cursor, _) => cursor is null
            ? ValueTask.FromResult(new ListToolsResult { Tools = [CatalogTool()], NextCursor = "broken-page" })
            : throw new McpException(RemoteSecret));
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig { Url = server.Url() });
        Assert.False(result.Success);
        Assert.True(result.Connected);
        Assert.Equal("Catalog test server", result.ServerName);
        Assert.NotNull(result.ProtocolVersion);
        Assert.Empty(result.Tools);
        Assert.Contains("catalog", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RemoteSecret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("tools/call", server.Methods);
    }

    [Fact]
    public async Task McpTest_HttpFailureDoesNotExposeUrlCredentialsQueryOrResponse()
    {
        await using McpTestServer server = await McpTestServer.StartAsync();
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        string url = server.Url().Replace("http://", $"http://user:{RemoteSecret}@", StringComparison.Ordinal)
            .Replace("/mcp", $"/denied?token={RemoteSecret}", StringComparison.Ordinal);
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig { Url = url });
        Assert.False(result.Success);
        Assert.False(result.Connected);
        Assert.Null(result.ServerName);
        Assert.Empty(result.Tools);
        Assert.Contains("HTTP 401", result.Error, StringComparison.Ordinal);
        string payload = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(RemoteSecret, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/denied", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpTest_ExplicitProtocolVersionMustActuallyBeNegotiated()
    {
        await using McpTestServer server = await McpTestServer.StartAsync();
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig
        {
            Url = server.Url(), ProtocolVersion = "2099-01-01"
        });
        Assert.False(result.Success);
        Assert.False(result.Connected);
        Assert.Empty(result.Tools);
        Assert.Contains("initialization", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2099-01-01", Assert.Single(server.RequestedVersions));
    }

    [Fact]
    public async Task McpTest_HttpModeDoesNotFallBackToLegacySse()
    {
        await using McpTestServer server = await McpTestServer.StartAsync();
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig
        {
            Url = server.Url(McpServerType.SSE), Type = McpServerType.Http
        });
        Assert.False(result.Success);
        Assert.False(result.Connected);
        Assert.DoesNotContain("GET /mcp/sse", server.Requests);
    }

    [Theory]
    [InlineData("", "Http", null)]
    [InlineData("   ", "Http", null)]
    [InlineData("/mcp", "Http", null)]
    [InlineData("file:///private/secret", "Http", null)]
    [InlineData("ftp://example.test/mcp", "Http", null)]
    [InlineData("https://example.test/mcp", "Stdio", null)]
    [InlineData("https://example.test/mcp", "123", null)]
    [InlineData("https://example.test/mcp", "Http", "invalid")]
    [InlineData("https://example.test/mcp", "Http", "2025-02-30")]
    [InlineData("https://example.test/mcp", "Http", "2025-1-1")]
    [InlineData("https://example.test/mcp", "Http", "")]
    public async Task McpTest_RejectsInvalidInputs(string url, string type, string? version)
    {
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        using HttpResponseMessage response = await admin.Client.PostAsJsonAsync(McpTestRoute,
            new { name = "draft", url, type, protocolVersion = version });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task McpTest_RequiresAuthenticationBeforeContactingServer()
    {
        await using McpTestServer server = await McpTestServer.StartAsync();
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        admin.Client.DefaultRequestHeaders.Remove("X-Test-Tenant");
        using HttpResponseMessage response = await admin.Client.PostAsJsonAsync(McpTestRoute,
            new McpServerConfig { Url = server.Url() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(server.Methods);
    }

    [Fact]
    public async Task McpTest_TimeoutCancelsOutboundRequestAndReturnsSafeFailure()
    {
        await using McpTestServer server = await McpTestServer.StartAsync();
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        var stopwatch = Stopwatch.StartNew();
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig { Url = server.BaseUrl + "/stall" });
        Assert.False(result.Success);
        Assert.False(result.Connected);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(result.LatencyMs, 14000, 22000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(25));
        await server.StalledRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task McpTest_RequestCancellationReachesOutboundConnection()
    {
        await using McpTestServer server = await McpTestServer.StartAsync();
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> request = admin.Client.PostAsJsonAsync(McpTestRoute,
            new McpServerConfig { Url = server.BaseUrl + "/stall" }, cancellation.Token);
        await server.StalledRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var _ = await request; });
        await server.StalledRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task McpTest_SessionCleanupUsesTheSameDeadline()
    {
        await using McpTestServer server = await McpTestServer.StartAsync(stallCleanup: true);
        await using McpAdminHost admin = await McpAdminHost.StartAsync();
        var stopwatch = Stopwatch.StartNew();
        McpConnectionTestResult result = await admin.TestAsync(new McpServerConfig { Url = server.Url() });
        Assert.True(result.Connected);
        Assert.InRange(result.LatencyMs, 0, 22000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(25));
        Assert.True(server.StalledRequestStarted.Task.IsCompleted);
        await server.StalledRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Tool CatalogTool() => new()
    {
        Name = "search", Title = "Search title", Description = "Search the catalog",
        InputSchema = Schema("""
            {"type":"object","required":["query"],"properties":{"query":{"type":"string"},"options":{"type":"object","required":["limit"],"properties":{"limit":{"type":"integer"}}}}}
            """),
        OutputSchema = Schema("""
            {"type":"object","required":["items"],"properties":{"items":{"type":"array","items":{"type":"string"}}}}
            """)
    };

    private static JsonElement Schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class McpAdminHost(WebApplication app, HttpClient client, Mock<IMcpConfigRepository> repository) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;

        internal static async Task<McpAdminHost> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var repository = new Mock<IMcpConfigRepository>(MockBehavior.Strict);
            builder.Services.AddSingleton(new McpProfileManagementService(repository.Object));
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TenantAuthenticationHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<AgentUserContextMiddleware>();
            app.MapConfigurationEndpoints();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-a");
            return new McpAdminHost(app, client, repository);
        }

        internal async Task<McpConnectionTestResult> TestAsync(McpServerConfig config)
        {
            using HttpResponseMessage response = await Client.PostAsJsonAsync(McpTestRoute, config);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<McpConnectionTestResult>())!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            repository.VerifyNoOtherCalls();
        }
    }

    private sealed class McpTestServer(WebApplication app) : IAsyncDisposable
    {
        internal string BaseUrl => app.Urls.Single();
        internal ConcurrentQueue<string> Methods { get; } = new();
        internal ConcurrentQueue<string> RequestedVersions { get; } = new();
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal TaskCompletionSource StalledRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StalledRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Url(McpServerType type = McpServerType.Http) => BaseUrl + (type == McpServerType.SSE ? "/mcp/sse" : "/mcp");

        internal static async Task<McpTestServer> StartAsync(
            Func<string?, CancellationToken, ValueTask<ListToolsResult>>? list = null, bool stallCleanup = false)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            IMcpServerBuilder mcp = builder.Services.AddMcpServer(options =>
                options.ServerInfo = new() { Name = "Catalog test server", Version = "2.3.4" })
#pragma warning disable MCP9004 // Legacy SSE compatibility is exercised only against this isolated loopback test server.
                .WithHttpTransport(options => options.EnableLegacySse = true);
#pragma warning restore MCP9004
            if (list is not null)
                mcp.WithListToolsHandler((request, ct) => list(request.Params?.Cursor, ct));
            WebApplication app = builder.Build();
            var server = new McpTestServer(app);
            app.Use(async (context, next) =>
            {
                server.Requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
                if (stallCleanup && context.Request.Method == "DELETE" && context.Request.Path == "/mcp")
                {
                    await server.WaitForCancellationAsync(context);
                    return;
                }
                if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments("/mcp"))
                {
                    context.Request.EnableBuffering();
                    using JsonDocument request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    if (request.RootElement.TryGetProperty("method", out JsonElement method))
                    {
                        server.Methods.Enqueue(method.GetString()!);
                        if (method.GetString() == "initialize")
                            server.RequestedVersions.Enqueue(request.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString()!);
                    }
                    context.Request.Body.Position = 0;
                }
                await next();
            });
            app.MapMcp("/mcp");
            app.MapPost("/denied", () => Results.Text(RemoteSecret, statusCode: 401));
            app.MapPost("/stall", (HttpContext context) => server.WaitForCancellationAsync(context));
            await app.StartAsync();
            return server;
        }

        private async Task WaitForCancellationAsync(HttpContext context)
        {
            StalledRequestStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                StalledRequestCancelled.TrySetResult();
            }
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
