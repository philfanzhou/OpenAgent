using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OpenAgent.Router.Tests.Integration;

public sealed class RouterHostFixture : IAsyncLifetime
{
    public TestEngineHost PrimaryEngine { get; } = new("primary-engine");

    public Task InitializeAsync() => PrimaryEngine.StartAsync();

    public Task DisposeAsync() => PrimaryEngine.DisposeAsync().AsTask();

    internal RouterApplicationFactory CreateFactory(
        string? engineEndpoint = null,
        int requestsPerSecond = 100,
        int burstCapacity = 200,
        IReadOnlyDictionary<string, string?>? settings = null) =>
        new(
            engineEndpoint ?? PrimaryEngine.Endpoint,
            requestsPerSecond,
            burstCapacity,
            settings);
}

internal sealed class RouterApplicationFactory(
    string engineEndpoint,
    int requestsPerSecond,
    int burstCapacity,
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic",
                ["Authentication:AllowDevelopmentAnonymous"] = "false",
                ["Authentication:DevelopmentTenantId"] = "tenant-1",
                ["RouterSettings:IntentRecognition:Enabled"] = "false",
                ["RouterSettings:IntentRecognition:FallbackAgentId"] = "default",
                ["RouterSettings:Routing:EngineEndpoint"] = engineEndpoint,
                ["RouterSettings:RateLimiting:RequestsPerSecond"] = requestsPerSecond.ToString(),
                ["RouterSettings:RateLimiting:BurstCapacity"] = burstCapacity.ToString()
            });
            if (settings is not null)
                configuration.AddInMemoryCollection(settings);
        });
    }
}

public sealed class TestEngineHost(string responseName, bool sendHeartbeats = false) : IAsyncDisposable
{
    private static readonly byte[] DownloadBytes = [0x00, 0x01, 0x7f, 0x80, 0xfe, 0xff];
    private readonly TaskCompletionSource<bool> _sseCanceled = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _application;
    private int _chatRequestCount;

    public string Endpoint { get; private set; } = string.Empty;

    public int ChatRequestCount => Volatile.Read(ref _chatRequestCount);

    public string? LastUserId { get; private set; }

    public string? LastTenantId { get; private set; }

    public string? LastAuthorization { get; private set; }


    public string? LastCatalogTenantId { get; private set; }

    public string? UploadedFileName { get; private set; }

    public string? UploadedContentType { get; private set; }

    public byte[]? UploadedBytes { get; private set; }

    public static ReadOnlyMemory<byte> ExpectedDownloadBytes => DownloadBytes;

    public async Task StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256L * 1024 * 1024)
            .UseUrls("http://127.0.0.1:0");
        WebApplication application = builder.Build();

        application.MapGet("/ready", () => Results.Ok());
        application.MapGet("/api/v1/agent/agents", (HttpContext context) =>
        {
            LastCatalogTenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
            LastAuthorization = context.Request.Headers.Authorization.FirstOrDefault();
            return Results.Json(new[]
            {
                new { agentId = "default", name = "Default", description = "Test agent" }
            });
        });
        application.MapPost("/api/v1/agent/chat", async context =>
        {
            Interlocked.Increment(ref _chatRequestCount);
            LastUserId = context.Request.Headers["X-User-Id"].FirstOrDefault();
            LastTenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
            LastAuthorization = context.Request.Headers.Authorization.FirstOrDefault();
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "public, max-age=60";
            await context.Response.WriteAsync(
                $$"""{"message":"{{responseName}}"}""",
                context.RequestAborted).ConfigureAwait(false);
        });
        application.MapPost("/api/v1/agent/chat/sse", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync(
                "event: token\ndata: first\n\n",
                context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            if (sendHeartbeats)
            {
                for (int heartbeat = 0; heartbeat < 4; heartbeat++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted).ConfigureAwait(false);
                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
                await context.Response.WriteAsync("event: done\ndata: {}\n\n", context.RequestAborted).ConfigureAwait(false);
                return;
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                _sseCanceled.TrySetResult(true);
            }
        });
        application.MapPost("/api/v1/agent/chat/stream", context =>
        {
            // 模拟 Engine 准入拒绝：队列已满，在流开始之前就返回 429。
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "1";
            return context.Response.WriteAsJsonAsync(new
            {
                type = "https://error.agent.com/runtime-queue-full",
                title = "RuntimeQueueFull",
                status = StatusCodes.Status429TooManyRequests,
                detail = "Runtime capacity is busy. Retry after the indicated delay.",
                traceId = "trace-queue-full",
                errorCode = 8201,
                retryAfterSeconds = 1,
            }, context.RequestAborted);
        });
        application.MapGet("/api/v1/agent/conversations", () => Results.Json(Array.Empty<object>()));
        application.MapPost("/api/v1/agent/files", async context =>
        {
            IFormCollection form = await context.Request.ReadFormAsync(
                context.RequestAborted).ConfigureAwait(false);
            IFormFile file = Assert.Single(form.Files);
            UploadedFileName = file.FileName;
            UploadedContentType = file.ContentType;
            await using var content = new MemoryStream();
            await file.CopyToAsync(content, context.RequestAborted).ConfigureAwait(false);
            UploadedBytes = content.ToArray();
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"fileId\":\"file-1\"}",
                context.RequestAborted).ConfigureAwait(false);
        });
        application.MapGet("/api/v1/agent/files/file-1/download", async context =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = "attachment; filename=payload.bin";
            await context.Response.Body.WriteAsync(
                DownloadBytes,
                context.RequestAborted).ConfigureAwait(false);
        });

        await application.StartAsync().ConfigureAwait(false);
        IServer server = application.Services.GetRequiredService<IServer>();
        Endpoint = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _application = application;
    }

    public Task WaitForSseCancellationAsync(CancellationToken cancellationToken) =>
        _sseCanceled.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_application == null)
        {
            return;
        }

        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
