namespace OpenAgent.Hosting;

public class AgentHostOptions
{
    public bool EnableCors { get; set; } = true;
    public bool EnableSwagger { get; set; } = true;

    /// <summary>是否在非 Development 环境暴露 Swagger UI（测试/预发环境用，生产保持关闭）。</summary>
    public bool SwaggerExposeInNonDevelopment { get; set; }
    public bool EnableHealthChecks { get; set; } = true;
    public bool EnableJwtAuth { get; set; } = true;
    public bool EnableOpenTelemetry { get; set; } = true;
    public string CorsPolicyName { get; set; } = "AgentCorsPolicy";
    public string[] CorsAllowedOrigins { get; set; } = ["http://localhost:5173"];
    public string HealthCheckLivePath { get; set; } = "/health";
    public string HealthCheckReadyPath { get; set; } = "/ready";
    public string ServiceName { get; set; } = "openagent-service";
    public string ServiceVersion { get; set; } = "1.0.0";
    public string OpenTelemetrySource { get; set; } = "OpenAgent";
}
