using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Debugging;
using Serilog.Formatting.Json;

namespace OpenAgent.Hosting;

public static class SerilogHostBuilderExtensions
{
    /// <summary>
    /// 控制台输出格式。默认 <see cref="Plain"/> 保持既有人工阅读的模板不变；
    /// <see cref="Json"/> 输出单行 JSON，使 ServiceName / TraceId / ConversationId
    /// 等属性成为可查询字段（日志中心按 trace/会话关联需要它）。
    /// </summary>
    public static class ConsoleFormats
    {
        public const string Plain = "Plain";
        public const string Json = "Json";
    }

    /// <summary>
    /// 装配 Serilog。控制台格式由 <c>Serilog:ConsoleFormat</c> 控制（<c>Plain</c> | <c>Json</c>）。
    ///
    /// 默认输出是 Serilog 的纯文本模板，只渲染消息与内联属性：
    /// <c>Enrich.WithProperty</c> 添加的 ServiceName/InstanceId，以及
    /// <c>BeginScope</c> 里的 TraceId/ConversationId 都不会出现在控制台行里
    /// （已用 .NET 8 实测确认）。依赖这些字段的采集——Promtail → Loki 按
    /// trace/会话关联——必须启用 JSON 格式，配置写法见 <see cref="ApplyConsoleFormat"/>。
    /// </summary>
    public static IHostBuilder UseAgentSerilog(
        this IHostBuilder hostBuilder,
        string serviceName,
        string serviceVersion = "1.0.0")
    {
        hostBuilder.ConfigureLogging(logging => logging.ClearProviders());
        return hostBuilder.UseSerilog(
            (context, services, loggerConfiguration) =>
            {
                loggerConfiguration
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("ServiceName", serviceName)
                    .Enrich.WithProperty("ServiceVersion", serviceVersion)
                    .Enrich.WithProperty("InstanceId", Environment.MachineName)
                    .ReadFrom.Configuration(context.Configuration);

                ApplyConsoleFormat(loggerConfiguration, context.Configuration);

                loggerConfiguration.ReadFrom.Services(services);
            },
            writeToProviders: true);
    }

    /// <summary>
    /// 在配置已读取之后切换控制台输出。
    ///
    /// Serilog 的 <c>LoggerSinkConfiguration</c> 没有清除已注册 sink 的 API，
    /// 所以 JSON 模式不能"替换"appsettings 里的 Console sink；正确做法是让
    /// appsettings 的 <c>WriteTo</c> 本身携带 formatter：
    ///
    /// <code>
    /// "Serilog": {
    ///   "ConsoleFormat": "Json",
    ///   "WriteTo": [{ "Name": "Console",
    ///                 "Args": { "formatter": "Serilog.Formatting.Json.JsonFormatter, Serilog" } }]
    /// }
    /// </code>
    ///
    /// 本方法只做校验与兜底：JSON 模式下如果配置里仍是纯文本 Console（没有
    /// formatter 参数），追加一个 JSON sink 以保证字段可用——代价是该事件输出两行，
    /// 因此这种情况下会通过 Serilog SelfLog 给出明确告警，提示改用上面的配置。
    /// </summary>
    internal static void ApplyConsoleFormat(LoggerConfiguration loggerConfiguration, IConfiguration? configuration)
    {
        if (!IsJsonConsole(configuration))
        {
            return;
        }

        bool configuresFormatter = HasConsoleFormatterArgument(configuration);
        if (!configuresFormatter)
        {
            SelfLog.WriteLine(
                "Serilog:ConsoleFormat=Json but Serilog:WriteTo Console has no formatter argument; "
                + "appending a JSON sink (events will be printed twice). "
                + "Set Args.formatter = Serilog.Formatting.Json.JsonFormatter, Serilog.");
            loggerConfiguration.WriteTo.Sink(new JsonConsoleSink());
        }
    }

    /// <summary>
    /// 判断配置里的 Console sink 是否已带 <c>formatter</c> 参数。
    /// 支持 <c>WriteTo[0].Args.formatter</c> 形式（Serilog.Settings.Configuration）。
    /// </summary>
    internal static bool HasConsoleFormatterArgument(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return false;
        }

        foreach (IConfigurationSection sink in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            string? name = sink["Name"];
            if (name is null || !string.Equals(name, "Console", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sink["Args:formatter"]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 解析控制台格式；未配置或空值按 <see cref="ConsoleFormats.Plain"/> 处理。
    /// 未知取值直接抛出，避免拼写错误悄悄退回纯文本、导致日志字段静默消失。
    /// </summary>
    internal static bool IsJsonConsole(IConfiguration? configuration)
    {
        string? raw = configuration?["Serilog:ConsoleFormat"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim() switch
        {
            var f when string.Equals(f, ConsoleFormats.Json, StringComparison.OrdinalIgnoreCase) => true,
            var f when string.Equals(f, ConsoleFormats.Plain, StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new InvalidOperationException(
                $"Unsupported Serilog:ConsoleFormat '{raw}'. Expected {ConsoleFormats.Plain} or {ConsoleFormats.Json}.")
        };
    }

    /// <summary>单行 JSON 输出到控制台（容器 stdout）。</summary>
    private sealed class JsonConsoleSink : Serilog.Core.ILogEventSink
    {
        private readonly JsonFormatter _formatter = new();

        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            _formatter.Format(logEvent, Console.Out);
            Console.Out.WriteLine();
            Console.Out.Flush();
        }
    }
}