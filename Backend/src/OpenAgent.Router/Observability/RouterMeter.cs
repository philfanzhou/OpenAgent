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

    /// <summary>转发失败总数，按 action/forwarder_error 分组</summary>
    private static readonly Counter<long> ForwardingFailuresTotal = Meter.CreateCounter<long>("openagent_router_forwarding_failures_total");
    private static readonly Counter<long> DiscoveryRefreshTotal = Meter.CreateCounter<long>("openagent_router_discovery_refresh_total");
    private static readonly Counter<long> DiscoverySelectionsTotal = Meter.CreateCounter<long>("openagent_router_discovery_selections_total");
    private static readonly Counter<long> RateLimitDecisionsTotal = Meter.CreateCounter<long>("openagent_router_rate_limit_decisions_total");
    private static readonly Histogram<int> DiscoveryEngineCount = Meter.CreateHistogram<int>("openagent_router_discovery_engine_count");
    private static readonly Counter<long> DownstreamProbesTotal = Meter.CreateCounter<long>("openagent_router_downstream_probes_total");
    private static readonly Counter<long> ProviderSelectionsTotal = Meter.CreateCounter<long>(
        "openagent_router_provider_selections_total");
    private static readonly Counter<long> AclDenialsTotal = Meter.CreateCounter<long>(
        "openagent_router_acl_denials_total");
    private static readonly Counter<long> CacheHitsTotal = Meter.CreateCounter<long>(
        "openagent_router_cache_hits_total");

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

    public static void RecordDiscoveryRefresh(string outcome, int engineCount)
    {
        DiscoveryRefreshTotal.Add(1, new TagList { { "outcome", Normalize(outcome) } });
        DiscoveryEngineCount.Record(engineCount, new TagList { { "outcome", Normalize(outcome) } });
    }

    public static void RecordDiscoverySelection(string intent, string source)
    {
        DiscoverySelectionsTotal.Add(1, new TagList
        {
            { "intent", Normalize(intent) },
            { "source", Normalize(source) }
        });
    }

    public static void RecordRateLimitDecision(RateLimitDecision decision)
    {
        RateLimitDecisionsTotal.Add(1, new TagList
        {
            { "outcome", decision.IsAllowed ? "allowed" : "denied" },
            { "source", Normalize(decision.Source) },
            { "degraded", decision.IsDegraded }
        });
    }

    public static void RecordDownstreamProbe(string outcome)
    {
        DownstreamProbesTotal.Add(1, new TagList { { "outcome", Normalize(outcome) } });
    }

    public static void RecordProviderSelection(string source)
    {
        ProviderSelectionsTotal.Add(1, new TagList
        {
            { "source", NormalizeSelectionSource(source) }
        });
    }

    public static void RecordAclDenial() =>
        AclDenialsTotal.Add(1, new TagList { { "reason", "agent_acl" } });

    public static void RecordCacheHit(string cache)
    {
        CacheHitsTotal.Add(1, new TagList
        {
            { "cache", NormalizeCache(cache) }
        });
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();

    private static string NormalizeSelectionSource(string value) => value.Trim().ToLowerInvariant() switch
    {
        "explicit" => "explicit",
        "conversation" => "conversation",
        "intent" => "intent",
        "fallback" => "fallback",
        _ => "other"
    };

    private static string NormalizeCache(string value) => value.Trim().ToLowerInvariant() switch
    {
        "idempotency" => "idempotency",
        "query" => "query",
        _ => "other"
    };

}
