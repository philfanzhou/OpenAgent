using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenAgent.Router;

/// <summary>
/// Router 路由指标仪表盘，通过 OpenTelemetry Meter 暴露 Prometheus 指标。
/// Prometheus 通过 /metrics 端点主动拉取（Pull 模式）。
/// </summary>
internal static class RouterMeter
{
    public const string MeterName = "OpenAgent.Router";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>路由请求总数，按 action/status 分组</summary>
    private static readonly Counter<long> RoutesTotal = Meter.CreateCounter<long>("openagent_router_routes_total");
    /// <summary>转发失败总数，按 action/forwarder_error 分组</summary>
    private static readonly Counter<long> ForwardingFailuresTotal = Meter.CreateCounter<long>("openagent_router_forwarding_failures_total");

    /// <summary>
    /// 记录一次路由请求
    /// </summary>
    public static void RecordRoute(string action, string status)
    {
        RoutesTotal.Add(1, new TagList
        {
            { "action", Normalize(action) },
            { "status", Normalize(status) }
        });
    }

    /// <summary>
    /// 记录一次转发失败
    /// </summary>
    public static void RecordForwardingFailure(string action, string forwarderError)
    {
        ForwardingFailuresTotal.Add(1, new TagList
        {
            { "action", Normalize(action) },
            { "forwarder_error", Normalize(forwarderError) }
        });
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();
}
