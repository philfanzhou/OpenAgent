using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenAgent.Runtime.Tests;

internal sealed class ModelFixture(WebApplication app, ConcurrentQueue<Exception> failures) : IAsyncDisposable
{
    internal string Endpoint => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/v1";

    internal static async Task<ModelFixture> StartAsync(Func<HttpContext, JsonElement, Task> respond)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        ConcurrentQueue<Exception> failures = new();
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body);
            // OpenCode 的标题生成是独立的辅助请求，不作为受测的用户推理轮次。
            if (document.RootElement.GetProperty("messages").EnumerateArray().Any(message =>
                message.GetProperty("role").GetString() == "system"
                && message.GetProperty("content").ValueKind == JsonValueKind.String
                && message.GetProperty("content").GetString()!.StartsWith("You are a title generator", StringComparison.Ordinal)))
            {
                await TextAsync(context, "Local test");
                return;
            }
            try
            {
                await respond(context, document.RootElement);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 服务端断言不能被 Kestrel 吞成可重试 500；直接结束模型请求并在释放时报告原错。
                failures.Enqueue(error);
                context.Response.StatusCode = 400;
            }
        });
        await app.StartAsync();
        return new ModelFixture(app, failures);
    }

    internal static Task TextAsync(HttpContext context, string text) => ChunkAsync(context, new { role = "assistant", content = text }, "stop");

    internal static async Task ChunkAsync(HttpContext context, object delta, string finish)
    {
        context.Response.ContentType = "text/event-stream";
        object Chunk(object value, string? reason) => new
        {
            id = "test",
            @object = "chat.completion.chunk",
            created = 1,
            model = "fixture",
            choices = new[] { new { index = 0, delta = value, finish_reason = reason } }
        };
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(Chunk(delta, null)) + "\n\n");
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(Chunk(new
        {
        }, finish)) + "\n\ndata: [DONE]\n\n");
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        if (failures.TryDequeue(out Exception? error))
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}

internal sealed class OpenCodeFactAttribute : FactAttribute
{
    public OpenCodeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE")))
        {
            Skip = "Set OPENAGENT_TEST_OPENCODE to OpenCode 1.18.35 for real harness tests.";
        }
    }
}
