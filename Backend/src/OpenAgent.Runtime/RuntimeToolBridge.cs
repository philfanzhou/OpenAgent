using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
/// 宿主共享的 MCP 桥。组合根调用 Mount 后复用主站已有端口（自闸门拒绝非 loopback
/// 远端与非本轮 bearer），不再额外监听；未挂载或主站绑定不可 loopback 直连时，
/// 回退为自建 loopback 端点。每轮以独立随机 bearer 注册工具集，释放或整轮取消即吊销。
/// </summary>
internal sealed class RuntimeToolBridge : IAsyncDisposable
{
    internal const string BridgePath = "/runtime-tools";

    // 认证中间件按请求解析出的注册经执行上下文传给 MCP 处理器；未认证请求到不了处理器。
    private static readonly AsyncLocal<Registration?> Current = new();

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private WebApplication? _mounted;
    private string? _endpoint;

    /// <summary>注册本轮工具集并返回携带独立 bearer 的 MCP server 定义；释放租约即吊销该 bearer。</summary>
    internal async Task<RuntimeToolLease> RegisterAsync(
        IReadOnlyList<RuntimeTool> definitions,
        CancellationToken turnCancellation)
    {
        Dictionary<string, RuntimeTool> tools = definitions.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        string endpoint = MountedEndpoint() ?? await StartOnceAsync().ConfigureAwait(false);
        // 256 bit 随机 bearer 兼作注册键；身份不取自模型参数，不接受客户端传入。
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _registrations[token] = new Registration(tools, turnCancellation);
        return new RuntimeToolLease(this, token, new McpServer("openagent")
        {
            Type = "http",
            Url = endpoint,
            Headers = [new("Authorization", "Bearer " + token)]
        });
    }

    internal ValueTask ReleaseAsync(string token)
    {
        _registrations.TryRemove(token, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>把桥挂到主站应用：闸门插在调用点位置，端点与公共路由共用主站端口。</summary>
    internal void Mount(WebApplication app)
    {
        if (_mounted is not null)
        {
            throw new InvalidOperationException("Runtime tool bridge is already mounted.");
        }
        app.Use((context, next) => GateAsync(context, next));
        app.MapMcp(BridgePath);
        _mounted = app;
    }

    /// <summary>主站完成绑定后地址才可用；拿不到可 loopback 直连的 http 绑定时返回 null 回退自建。</summary>
    private string? MountedEndpoint()
    {
        if (_mounted is not { } app)
        {
            return null;
        }
        IServerAddressesFeature? addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        foreach (string address in addresses?.Addresses ?? [])
        {
            if (TryLoopbackBase(address, out string? loopback))
            {
                return loopback + BridgePath;
            }
        }
        return null;
    }

    private static bool TryLoopbackBase(string address, [NotNullWhen(true)] out string? loopback)
    {
        loopback = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http")
        {
            return false;
        }
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
        {
            loopback = "http://127.0.0.1:" + uri.Port;
            return true;
        }
        if (uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1")
        {
            loopback = "http://" + uri.Authority;
            return true;
        }
        return false;
    }

    private async Task<string> StartOnceAsync()
    {
        if (_application is { } running)
        {
            return _endpoint!;
        }
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_application is { } started)
            {
                return _endpoint!;
            }
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production"
            });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            AddBridgeServer(builder.Services);
            WebApplication app = builder.Build();
            app.Use((context, next) => GateAsync(context, next));
            app.MapMcp(BridgePath);
            try
            {
                // 兜底站点不绑定单轮取消令牌；启动失败时还原为未启动，下一轮可重试。
                await app.StartAsync().ConfigureAwait(false);
                _endpoint = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + BridgePath;
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _application = app;
            return _endpoint;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task GateAsync(HttpContext context, Func<Task> next)
    {
        if (!context.Request.Path.StartsWithSegments(BridgePath))
        {
            await next().ConfigureAwait(false);
            return;
        }
        // 主站端口可能对外可达：非 loopback 远端一律 404，不暴露路由存在性。
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
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
            await next().ConfigureAwait(false);
        }
        finally
        {
            Current.Value = null;
        }
    }

    /// <summary>MCP server 注册与处理器：挂载主站与自建兜底共用同一套，处理器只读本轮注册。</summary>
    internal static void AddBridgeServer(IServiceCollection services)
    {
        services.AddMcpServer(options => options.ServerInfo = new() { Name = "OpenAgent", Version = "1" })
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
            // 挂载的主站应用生命周期归宿主，这里只清空注册；仅回收自建兜底站点。
            _mounted = null;
            _registrations.Clear();
            if (_application is not { } application)
            {
                return;
            }
            _application = null;
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
