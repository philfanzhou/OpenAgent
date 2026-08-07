namespace OpenAgent.Hosting;

public class AgentHostOptions
{
    public bool EnableCors { get; set; } = true;
    public bool EnableSwagger { get; set; } = true;
    public bool EnableHealthChecks { get; set; } = true;
    public bool EnableJwtAuth { get; set; } = true;
    public bool EnableOpenTelemetry { get; set; } = true;
    public string CorsPolicyName { get; set; } = "AgentCorsPolicy";
    public string[] CorsAllowedOrigins { get; set; } = ["http://localhost:5173"];
    public string HealthCheckLivePath { get; set; } = "/health";
    public string HealthCheckReadyPath { get; set; } = "/ready";
    public string ServiceName { get; set; } = "agent-service";
    public string ServiceVersion { get; set; } = "1.0.0";
    public string OpenTelemetrySource { get; set; } = "OpenAgent";
}
