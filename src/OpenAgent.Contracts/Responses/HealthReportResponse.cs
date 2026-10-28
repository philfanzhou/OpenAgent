namespace OpenAgent.Contracts.Responses;

/// <summary>健康检查聚合报告（GET /health/report）。</summary>
public sealed class HealthReportResponse
{
    /// <summary>聚合状态：Unhealthy / Degraded / Healthy。</summary>
    public required string Status { get; init; }

    public required string Service { get; init; }

    public double TotalDurationMs { get; init; }

    public IReadOnlyList<HealthReportItemResponse> Items { get; init; } = [];
}

/// <summary>单项健康检查结果。</summary>
public sealed class HealthReportItemResponse
{
    public required string Key { get; init; }

    public required string Status { get; init; }

    public string? Detail { get; init; }

    public double LatencyMs { get; init; }

    public IReadOnlyDictionary<string, object> Data { get; init; } = new Dictionary<string, object>();
}
