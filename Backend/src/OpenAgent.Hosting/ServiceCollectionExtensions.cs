using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using OpenAgent.Hosting.Observability;
using OpenAgent.Hosting.Security;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting;

public static class ServiceCollectionExtensions
{
    private static readonly Serilog.ILogger BootstrapLogger = Log.ForContext(typeof(ServiceCollectionExtensions));

    public static IServiceCollection AddAgentHost(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AgentHostOptions>? configure = null)
    {
        var options = new AgentHostOptions();
        configure?.Invoke(options);
        string[] configuredOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (configuredOrigins.Length > 0)
        {
            options.CorsAllowedOrigins = configuredOrigins;
        }

        services.Configure<AgentHostOptions>(opt =>
        {
            opt.EnableCors = options.EnableCors;
            opt.EnableSwagger = options.EnableSwagger;
            opt.EnableHealthChecks = options.EnableHealthChecks;
            opt.EnableJwtAuth = options.EnableJwtAuth;
            opt.EnableOpenTelemetry = options.EnableOpenTelemetry;
            opt.CorsPolicyName = options.CorsPolicyName;
            opt.CorsAllowedOrigins = options.CorsAllowedOrigins;
            opt.HealthCheckLivePath = options.HealthCheckLivePath;
            opt.HealthCheckReadyPath = options.HealthCheckReadyPath;
            opt.ServiceName = options.ServiceName;
            opt.ServiceVersion = options.ServiceVersion;
            opt.OpenTelemetrySource = options.OpenTelemetrySource;
        });
        services.AddOptions<AgentAuthenticationOptions>()
            .Bind(configuration.GetSection("Authentication"));

        if (options.EnableCors)
        {
            services.AddCors(cors =>
            {
                cors.AddPolicy(options.CorsPolicyName, policy =>
                {
                    policy
                        .WithOrigins(options.CorsAllowedOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials()
                        .SetPreflightMaxAge(TimeSpan.FromMinutes(30));
                });
            });
        }

        if (options.EnableSwagger)
        {
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
        }

        services.AddControllers();

        if (options.EnableHealthChecks)
        {
            services.AddHealthChecks();
        }

        if (options.EnableJwtAuth)
        {
            services.AddAgentAuthentication(configuration);
        }

        if (options.EnableOpenTelemetry)
        {
            var otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"]
                ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            var serviceName = configuration["OpenTelemetry:ServiceName"] ?? options.ServiceName;
            var serviceVersion = configuration["OpenTelemetry:ServiceVersion"] ?? options.ServiceVersion;

            try
            {
                var openTelemetry = services.AddOpenTelemetry()
                    .ConfigureResource(resource => resource
                        .AddService(serviceName, serviceVersion: serviceVersion));

                openTelemetry.WithTracing(tracing =>
                {
                    tracing
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource(options.OpenTelemetrySource);

                    if (!string.IsNullOrEmpty(otlpEndpoint))
                    {
                        tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint));
                    }
                });

                openTelemetry.WithMetrics(metrics =>
                {
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddMeter("OpenAgent.Core")
                        .AddMeter("OpenAgent.Engine")
                        .AddPrometheusExporter();
                });

                BootstrapLogger.Information(
                    "OpenTelemetry configured. OtlpEndpoint={OtlpEndpoint}, PrometheusEnabled={PrometheusEnabled}, ServiceName={ServiceName}, ServiceVersion={ServiceVersion}, Source={OpenTelemetrySource}",
                    string.IsNullOrEmpty(otlpEndpoint) ? "(none)" : otlpEndpoint,
                    true,
                    serviceName,
                    serviceVersion,
                    options.OpenTelemetrySource);
            }
            catch (Exception ex)
            {
                BootstrapLogger.Warning(
                    ex,
                    "OpenTelemetry configuration failed. Endpoint={OtlpEndpoint}, ServiceName={ServiceName}, ServiceVersion={ServiceVersion}, ExceptionType={ExceptionType}",
                    otlpEndpoint,
                    serviceName,
                    serviceVersion,
                    ex.GetType().FullName);
            }
        }

        return services;
    }
}
