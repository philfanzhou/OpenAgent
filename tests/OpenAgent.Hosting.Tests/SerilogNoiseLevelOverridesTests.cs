using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace OpenAgent.Hosting.Tests;

/// <summary>
/// 验证随代码发布的 appsettings.json 真正压掉了高频噪音类别：
/// 每请求 2-4 条的 HTTP 连接日志、MCP SDK 连接日志、Redis 重连、YARP 转发日志。
/// 配置用文件本体加载（与 SerilogHostBuilderExtensions 相同的 ReadFrom.Configuration 路径），
/// 防止配置漂移后此降噪静默失效。
/// </summary>
public sealed class SerilogNoiseLevelOverridesTests
{
    [Fact]
    public void EngineAppSettings_SuppressesRoutineCategoryInformationLogs()
    {
        // Engine 侧噪音来源：HttpClientFactory 连接日志、ModelContextProtocol SDK、
        // StackExchange.Redis 重连。
        AssertRoutineCategoriesSuppressed(
            "OpenAgent.Engine.Host",
            "System.Net.Http.HttpClient.AgentLogin.LogicalHandler",
            "ModelContextProtocol.Client.McpClient",
            "StackExchange.Redis.ConnectionMultiplexer");
    }

    [Fact]
    public void RouterAppSettings_SuppressesRoutineCategoryInformationLogs()
    {
        // Router 侧噪音来源：意图识别用 HttpClient、YARP 转发、Redis 服务发现。
        AssertRoutineCategoriesSuppressed(
            "OpenAgent.Router",
            "System.Net.Http.HttpClient.AgentLogin.ClientHandler",
            "Yarp.ReverseProxy.Forwarder.HttpForwarder",
            "StackExchange.Redis.ConnectionMultiplexer");
    }

    private static void AssertRoutineCategoriesSuppressed(
        string hostProject,
        params string[] categories)
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", hostProject, "appsettings.json"));
        Assert.True(File.Exists(path), $"shipped appsettings not found: {path}");

        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddJsonFile(path, optional: false);
        IConfigurationRoot configuration = configurationBuilder.Build();
        var sink = new CollectingSink();
        using Logger logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Sink(sink)
            .CreateLogger();

        foreach (string category in categories)
        {
            ILogger contextual = logger.ForContext(
                Constants.SourceContextPropertyName,
                category);
            contextual.Information("Start processing HTTP request");
            contextual.Warning("genuine warning worth seeing");
        }

        Assert.DoesNotContain(
            sink.Events,
            entry => entry.RenderMessage().Contains(
                "Start processing HTTP request",
                StringComparison.Ordinal));
        Assert.Equal(categories.Length, sink.Events.Count(entry => entry.RenderMessage()
            .Contains("genuine warning", StringComparison.Ordinal)));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }
}
