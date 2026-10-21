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
using CallToolContext = ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams>;

namespace OpenAgent.Runtime;

/// <summary>
/// 随 ACP 进程常驻的 loopback MCP 桥宿主。端点与 bearer 随进程生成、存活期与进程一致：
/// 保活进程复用时不会留下已死端点引发 harness 热重连空转；每轮以租约替换工具注册与
/// 取消令牌，工具回调仍只绑定当前轮。进程租约保证同一时刻至多一个活动轮次。
/// </summary>
internal sealed class RuntimeToolBridgeHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly BridgeState _state;
    internal McpServer Server { get; }

    private RuntimeToolBridgeHost(WebApplication application, BridgeState state, McpServer server)
    {
        _application = application;
        _state = state;
        Server = server;
    }

    /// <summary>桥的可变状态：进程级 bearer 与当前活动轮次注册。</summary>
    private sealed class BridgeState
    {
        internal byte[] ExpectedAuthorization { get; init; } = [];

        internal object Gate { get; } = new();

        internal Registration? Active { get; set; }
    }

    /// <summary>一轮的工具表与取消联动；由租约挂载/摘除。</summary>
    internal sealed class Registration(IReadOnlyDictionary<string, RuntimeTool> tools, CancellationTokenSource linked)
    {
        internal IReadOnlyDictionary<string, RuntimeTool> Tools { get; } = tools;

        internal CancellationTokenSource Linked { get; } = linked;
    }

    /// <summary>注册本轮工具并接管桥；释放时摘除注册并停止本轮取消联动。</summary>
    internal BridgeLease Lease(IReadOnlyList<RuntimeTool> definitions, CancellationToken turnCancellation)
    {
        Dictionary<string, RuntimeTool> tools = definitions.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(turnCancellation);
        Registration registration = new(tools, linked);
        lock (_state.Gate)
        {
            _state.Active?.Linked.Dispose();
            _state.Active = registration;
        }
        return new BridgeLease(this, registration);
    }

    internal sealed class BridgeLease(RuntimeToolBridgeHost host, Registration registration) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            lock (host._state.Gate)
            {
                if (host._state.Active == registration)
                {
                    host._state.Active = null;
                }
            }
            registration.Linked.Dispose();
        }
    }

    internal static async Task<RuntimeToolBridgeHost> StartAsync(CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + token);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        BridgeState state = new() { ExpectedAuthorization = expected };
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "OpenAgent", Version = "1" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(ListTools(state)))
            .WithCallToolHandler((request, ct) => CallToolAsync(state, request, ct));
        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            byte[] actual = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            bool authorized;
            lock (state.Gate)
            {
                // 无活动轮次注册、本轮已取消或令牌不符一律拒绝；固定时间比较不泄露时序信息。
                authorized = state.Active is { Linked.IsCancellationRequested: false }
                    && CryptographicOperations.FixedTimeEquals(state.ExpectedAuthorization, actual);
            }
            if (!authorized)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapMcp("/tools");
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new RuntimeToolBridgeHost(app, state, new McpServer("openagent")
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

    private static ListToolsResult ListTools(BridgeState state)
    {
        Registration? active;
        lock (state.Gate)
        {
            active = state.Active;
        }
        return new ListToolsResult
        {
            Tools = (active?.Tools.Values ?? []).Select(tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJsonSchema)
            }).ToList()
        };
    }

    private static async ValueTask<CallToolResult> CallToolAsync(BridgeState state, CallToolContext request, CancellationToken ct)
    {
        Registration? active;
        lock (state.Gate)
        {
            active = state.Active;
        }
        if (request.Params is not { } parameters || active is null
            || !active.Tools.TryGetValue(parameters.Name, out RuntimeTool? tool))
        {
            return new CallToolResult { IsError = true, Content = [new McpText { Text = "Tool not found." }] };
        }
        // RequestAborted 与当前轮取消共同约束工具；轮次结束或取消后不能靠旧连接继续调用。
        using CancellationTokenSource invocation = CancellationTokenSource.CreateLinkedTokenSource(ct, active.Linked.Token);
        invocation.Token.ThrowIfCancellationRequested();
        Dictionary<string, object?> arguments = parameters.Arguments?.ToDictionary(x => x.Key, x => (object?)x.Value) ?? [];
        try
        {
            string result = await tool.InvokeAsync(arguments, invocation.Token).ConfigureAwait(false);
            return new CallToolResult { Content = [new McpText { Text = result }] };
        }
        catch (OperationCanceledException) when (invocation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 回调异常可能携带服务端路径/凭据；只把可读、无敏感数据的失败交给 Agent。
            return new CallToolResult { IsError = true, Content = [new McpText { Text = "Platform tool execution failed or access was denied." }] };
        }
    }

    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            await _application.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_state.Gate)
            {
                _state.Active?.Linked.Dispose();
                _state.Active = null;
            }
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
