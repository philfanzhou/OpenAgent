using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Protocol;
using McpText = ModelContextProtocol.Protocol.TextContentBlock;

namespace OpenAgent.Runtime;

/// <summary>本轮专属的 loopback MCP 桥。随机令牌随租约回收，不接受客户端传入身份。</summary>
internal sealed class RuntimeToolBridge(WebApplication application, McpServer server) : IAsyncDisposable
{
    internal McpServer Server { get; } = server;

    internal static async Task<RuntimeToolBridge> StartAsync(
        IReadOnlyList<RuntimeTool> definitions,
        CancellationToken turnCancellation)
    {
        Dictionary<string, RuntimeTool> tools = definitions.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "OpenAgent", Version = "1" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, ct) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = tools.Values.Select(tool => new Tool
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJsonSchema)
                }).ToList()
            }))
            .WithCallToolHandler(async (request, ct) =>
            {
                if (request.Params is not { } parameters || !tools.TryGetValue(parameters.Name, out RuntimeTool? tool))
                {
                    return new CallToolResult { IsError = true, Content = [new McpText { Text = "Tool not found." }] };
                }
                // RequestAborted 与整轮取消共同约束工具；取消后不能靠旧连接继续调用。
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, turnCancellation);
                linked.Token.ThrowIfCancellationRequested();
                Dictionary<string, object?> arguments = parameters.Arguments?.ToDictionary(x => x.Key, x => (object?)x.Value) ?? [];
                try
                {
                    string result = await tool.InvokeAsync(arguments, linked.Token).ConfigureAwait(false);
                    return new CallToolResult { Content = [new McpText { Text = result }] };
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 回调异常可能携带服务端路径/凭据；只把可读、无敏感数据的失败交给 Agent。
                    return new CallToolResult { IsError = true, Content = [new McpText { Text = "Platform tool execution failed or access was denied." }] };
                }
            });
        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            byte[] expected = Encoding.UTF8.GetBytes("Bearer " + token);
            byte[] actual = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (turnCancellation.IsCancellationRequested || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapMcp("/tools");
        try
        {
            await app.StartAsync(turnCancellation).ConfigureAwait(false);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new RuntimeToolBridge(app, new McpServer("openagent")
            {
                Type = "http",
                Url = address + "/tools",
                Headers = [new("Authorization", "Bearer " + token)]
            });
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            await application.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
