using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Hosting.Security;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenAgent.Hosting;

public static class ServiceCollectionExtensions
{
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
        services.AddHttpClient("AgentLogin", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });
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
            string? configuredEndpoint = configuration["OpenTelemetry:OtlpEndpoint"]
                ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            Uri? otlpEndpoint = null;
            if (!string.IsNullOrWhiteSpace(configuredEndpoint)
                && (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out otlpEndpoint)
                    || (otlpEndpoint.Scheme != Uri.UriSchemeHttp
                        && otlpEndpoint.Scheme != Uri.UriSchemeHttps)))
            {
                throw new InvalidOperationException(
                    $"OpenTelemetry:OtlpEndpoint must be an absolute HTTP(S) URI. Value: '{configuredEndpoint}'.");
            }

            string serviceName = configuration["OpenTelemetry:ServiceName"] ?? options.ServiceName;
            string serviceVersion = configuration["OpenTelemetry:ServiceVersion"] ?? options.ServiceVersion;
            var openTelemetry = services.AddOpenTelemetry()
                .ConfigureResource(resource => resource
                    .AddService(serviceName, serviceVersion: serviceVersion));

            services.AddLogging(logging => logging.AddOpenTelemetry(logs =>
            {
                logs.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName, serviceVersion: serviceVersion));
                logs.IncludeFormattedMessage = true;
                logs.IncludeScopes = true;
                logs.ParseStateValues = true;
                if (otlpEndpoint != null)
                {
                    logs.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
                }
            }));

            openTelemetry.WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(instrumentation =>
                        instrumentation.Filter = httpContext =>
                            !RequestTelemetryMiddleware.IsMetricsScrapePath(httpContext.Request.Path))
                    .AddHttpClientInstrumentation()
                    .AddSource(options.OpenTelemetrySource);

                if (otlpEndpoint != null)
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
                }
            });

            openTelemetry.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(options.OpenTelemetrySource)
                    .AddPrometheusExporter();

                if (otlpEndpoint != null)
                {
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
                }
            });
        }

        return services;
    }
}
