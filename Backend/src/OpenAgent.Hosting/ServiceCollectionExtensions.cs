using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Hosting.Security;
using OpenTelemetry.Exporter;
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
                        .WithExposedHeaders("X-OpenAgent-Selected-Agent-Id")
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
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigurePrimaryHttpMessageHandler(() =>
                HttpClientSecurity.CreateHttpClientHandler(configuration));
        });
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
            Uri? otlpEndpoint = ResolveOtlpEndpoint(configuration);

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
                    logs.AddOtlpExporter(exporter => ApplyOtlpEndpoint(exporter, otlpEndpoint, "v1/logs"));
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
                    tracing.AddOtlpExporter(exporter => ApplyOtlpEndpoint(exporter, otlpEndpoint, "v1/traces"));
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
                    metrics.AddOtlpExporter(exporter => ApplyOtlpEndpoint(exporter, otlpEndpoint, "v1/metrics"));
                }
            });
        }

        return services;
    }

    /// <summary>
    /// Resolves the OTLP endpoint. A blank <c>OpenTelemetry:OtlpEndpoint</c> value (deployments set the
    /// environment variable to an empty string by default) must not shadow the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> fallback. Returns <c>null</c> when no endpoint is configured.
    /// </summary>
    internal static Uri? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        string? configuredEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            configuredEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        }

        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp
                && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"OpenTelemetry:OtlpEndpoint / OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP(S) URI. Value: '{configuredEndpoint}'.");
        }

        return endpoint;
    }

    /// <summary>
    /// Assigning <see cref="OtlpExporterOptions.Endpoint"/> programmatically disables the SDK's
    /// signal-path appending, so with the http/protobuf protocol requests would go to the bare
    /// endpoint (for example <c>POST /</c>) and be rejected by spec-conformant collectors.
    /// Append the signal path ourselves when the endpoint carries none.
    /// </summary>
    internal static void ApplyOtlpEndpoint(OtlpExporterOptions exporter, Uri endpoint, string signalPath)
    {
        exporter.Endpoint = endpoint;
        if (exporter.Protocol == OtlpExportProtocol.HttpProtobuf && endpoint.AbsolutePath == "/")
        {
            exporter.Endpoint = new UriBuilder(endpoint) { Path = $"/{signalPath}" }.Uri;
        }
    }
}
