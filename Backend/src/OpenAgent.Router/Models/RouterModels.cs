namespace OpenAgent.Router;

public record RouteRequest(string Query, string? UserId = null, string? TenantId = null);

public record RouteResponse
{
    public string Intent { get; set; } = string.Empty;
    public string TargetEndpoint { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
}
