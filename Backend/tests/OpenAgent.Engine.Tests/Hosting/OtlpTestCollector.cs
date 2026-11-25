using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

// The OTLP SDK observes activities process-wide and several tests change OTLP environment variables.
[CollectionDefinition("OtelEnvironment", DisableParallelization = true)]
public sealed class OtelEnvironmentCollection;

internal sealed class OtlpTestCollector(WebApplication app) : IAsyncDisposable
{
    internal string Endpoint => app.Urls.Single();
    internal ConcurrentQueue<(string Path, byte[] Body)> Requests { get; } = new();

    internal static async Task<OtlpTestCollector> StartAsync(int traceStatusCode = StatusCodes.Status200OK)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        OtlpTestCollector collector = new(app);
        app.MapPost("/v1/{signal}", async (HttpContext context) =>
        {
            using MemoryStream body = new();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            collector.Requests.Enqueue((context.Request.Path.Value!, body.ToArray()));
            context.Response.ContentType = "application/x-protobuf";
            return Results.StatusCode(context.Request.Path == "/v1/traces" ? traceStatusCode : StatusCodes.Status200OK);
        });
        await app.StartAsync();
        return collector;
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
