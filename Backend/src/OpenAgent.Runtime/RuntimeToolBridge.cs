using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
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

/// <summary>
/// 宿主共享的 loopback MCP 桥：单一 Kestrel 端点随首个轮次惰性建立、宿主关闭时回收，
/// 每轮以独立随机 bearer 注册到端点，不再为每轮启动新站点。
/// </summary>
internal sealed class RuntimeToolBridge : IAsyncDisposable
{
    // 认证中间件按请求解析出的注册经执行上下文传给 MCP 处理器；未认证请求到不了处理器。
    private static readonly AsyncLocal<Registration?> Current = new();

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private string? _endpoint;

    /// <summary>注册本轮工具集并返回携带独立 bearer 的 MCP server 定义；释放租约即吊销该 bearer。</summary>
    internal async Task<RuntimeToolLease> RegisterAsync(
        IReadOnlyList<RuntimeTool> definitions,
        CancellationToken turnCancellation)
    {
        Dictionary<string, RuntimeTool> tools = definitions.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        await StartOnceAsync().ConfigureAwait(false);
        // 256 bit 随机 bearer 兼作注册键；身份不取自模型参数，不接受客户端传入。
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _registrations[token] = new Registration(tools, turnCancellation);
        return new RuntimeToolLease(this, token, new McpServer("openagent")
        {
            Type = "http",
            Url = _endpoint,
            Headers = [new("Authorization", "Bearer " + token)]
        });
    }

    internal ValueTask ReleaseAsync(string token)
    {
        _registrations.TryRemove(token, out _);
        return ValueTask.CompletedTask;
    }

    private async Task StartOnceAsync()
    {
        if (_application is { } running)
        {
            return;
        }
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_application is { } started)
            {
                return;
            }
            WebApplication application = Build();
            try
            {
                // 共享站点不绑定单轮取消令牌；启动失败时还原为未启动，下一轮可重试。
                await application.StartAsync().ConfigureAwait(false);
                _endpoint = application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/tools";
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _application = application;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private WebApplication Build()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "OpenAgent", Version = "1" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = Current.Value?.Tools.Values.Select(ToProtocolTool).ToList() ?? []
            }))
            .WithCallToolHandler(async (request, ct) =>
            {
                if (Current.Value is not { } registration
                    || request.Params is not { } parameters
                    || !registration.Tools.TryGetValue(parameters.Name, out RuntimeTool? tool))
                {
                    return new CallToolResult { IsError = true, Content = [new McpText { Text = "Tool not found." }] };
                }
                // RequestAborted 与整轮取消共同约束工具；取消后不能靠旧连接继续调用。
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, registration.TurnCancellation);
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
            // bearer 命中本轮注册才放行；已取消或已释放的轮次持有旧 token 同样拒绝。
            string header = context.Request.Headers.Authorization.ToString();
            Registration? registration = header.StartsWith("Bearer ", StringComparison.Ordinal)
                && _registrations.GetValueOrDefault(header["Bearer ".Length..]) is { } matched
                && !matched.TurnCancellation.IsCancellationRequested
                    ? matched
                    : null;
            if (registration is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            Current.Value = registration;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                Current.Value = null;
            }
        });
        app.MapMcp("/tools");
        return app;
    }

    private static Tool ToProtocolTool(RuntimeTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJsonSchema)
    };

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_application is not { } application)
            {
                return;
            }
            _application = null;
            _registrations.Clear();
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
        finally
        {
            _startGate.Release();
        }
    }

    private sealed record Registration(Dictionary<string, RuntimeTool> Tools, CancellationToken TurnCancellation);
}

/// <summary>本轮桥租约：释放即从共享端点吊销 bearer，整轮取消同样拒绝后续调用。</summary>
internal sealed class RuntimeToolLease(RuntimeToolBridge host, string token, McpServer server) : IAsyncDisposable
{
    internal McpServer Server { get; } = server;

    public ValueTask DisposeAsync() => host.ReleaseAsync(token);
}
