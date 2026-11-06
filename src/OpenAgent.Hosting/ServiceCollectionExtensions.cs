using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Hosting.Security;
using OpenTelemetry;
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
            // 仅保留最小必要配置：文档标题与 Bearer 安全定义（Authorize 按钮可用）。
            // 响应 schema 由端点的 TypedResults 返回类型自动推导，无需侵入端点代码。
            services.AddSwaggerGen(swaggerOptions =>
            {
                swaggerOptions.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                {
                    Title = $"{options.ServiceName} API",
                    Version = "v1"
                });
                swaggerOptions.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                    Description = "输入 JWT 访问令牌或 Bearer API Key（开发环境 Basic 模式同样经 Authorization 头发送）。"
                });
                swaggerOptions.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
                {
                    {
                        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                        {
                            Reference = new Microsoft.OpenApi.Models.OpenApiReference
                            {
                                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        Array.Empty<string>()
                    }
                });
            });
        }

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
            OtlpSettings otlp = OtlpSettings.FromConfiguration(configuration);
            // Exporters build their own HttpClient, so the deployment TLS policy
            // has to be handed to them explicitly. Without this an HTTPS
            // collector behind the internal (often self-signed) CA fails
            // certificate validation and every export is dropped silently —
            // the app looks healthy while no trace ever arrives.
            Func<HttpClient>? otlpHttpClientFactory =
                HttpClientSecurity.CreateHttpClientFactory(configuration);

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
                ApplyOtlpExporter(logs, otlp, OtlpSignal.Logs, "v1/logs", otlpHttpClientFactory);
            }));

            openTelemetry.WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(instrumentation =>
                        instrumentation.Filter = httpContext =>
                            !RequestTelemetryMiddleware.IsMetricsScrapePath(httpContext.Request.Path)
                            && !RequestTelemetryMiddleware.IsHealthProbePath(httpContext.Request.Path, options))
                    .AddHttpClientInstrumentation(instrumentation =>
                        instrumentation.FilterHttpRequestMessage = request =>
                            !RequestTelemetryMiddleware.IsHealthProbeUri(request.RequestUri, options))
                    .AddSource(options.OpenTelemetrySource)
                    .AddSource("OpenAgent.AgentFramework");

                ApplyOtlpExporter(tracing, otlp, OtlpSignal.Traces, "v1/traces", otlpHttpClientFactory);
            });

            openTelemetry.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(options.OpenTelemetrySource)
                    .AddPrometheusExporter();

                ApplyOtlpExporter(metrics, otlp, OtlpSignal.Metrics, "v1/metrics", otlpHttpClientFactory);
            });
        }

        return services;
    }

    /// <summary>
    /// Resolves the shared OTLP endpoint. A blank <c>OpenTelemetry:OtlpEndpoint</c> value (deployments set the
    /// environment variable to an empty string by default) must not shadow the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> fallback. Returns <c>null</c> when no endpoint is configured.
    /// Kept for callers that only need the shared value; per-signal resolution lives in
    /// <see cref="OtlpSettings.EndpointFor"/>.
    /// </summary>
    internal static Uri? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        string? configuredEndpoint = OtlpSettings.FromConfiguration(configuration).SharedEndpoint;
        return string.IsNullOrWhiteSpace(configuredEndpoint)
            ? null
            : OtlpSettings.ParseEndpoint(configuredEndpoint, OtlpSignal.Traces);
    }

    /// <summary>
    /// Configures one signal's OTLP exporter: endpoint (shared or per-signal),
    /// protocol and authentication headers.
    ///
    /// The exporter is only registered when that signal actually has an
    /// endpoint. This is what keeps the audit integration honest: pointing
    /// <c>OpenTelemetry:TracesEndpoint</c> at the masking gateway routes traces
    /// there while logs and metrics keep flowing to the normal Collector — and
    /// with no endpoint for a signal, nothing is exported, exactly as before.
    /// </summary>
    internal static void ApplyOtlpExporter(
        OpenTelemetryLoggerOptions logs, OtlpSettings settings, OtlpSignal signal, string signalPath,
        Func<HttpClient>? httpClientFactory = null)
    {
        if (TryCreateExporterOptions(settings, signal, signalPath, httpClientFactory) is { } exporter)
        {
            logs.AddOtlpExporter(configure: options => CopyExporterOptions(options, exporter));
        }
    }

    internal static void ApplyOtlpExporter(
        TracerProviderBuilder tracing, OtlpSettings settings, OtlpSignal signal, string signalPath,
        Func<HttpClient>? httpClientFactory = null)
    {
        if (TryCreateExporterOptions(settings, signal, signalPath, httpClientFactory) is { } exporter)
        {
            tracing.AddOtlpExporter(configure: options => CopyExporterOptions(options, exporter));
        }
    }

    internal static void ApplyOtlpExporter(
        MeterProviderBuilder metrics, OtlpSettings settings, OtlpSignal signal, string signalPath,
        Func<HttpClient>? httpClientFactory = null)
    {
        if (TryCreateExporterOptions(settings, signal, signalPath, httpClientFactory) is { } exporter)
        {
            metrics.AddOtlpExporter(configure: options => CopyExporterOptions(options, exporter));
        }
    }

    private static OtlpExporterOptions? TryCreateExporterOptions(
        OtlpSettings settings, OtlpSignal signal, string signalPath,
        Func<HttpClient>? httpClientFactory = null)
    {
        string? rawEndpoint = settings.EndpointFor(signal);
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return null;
        }

        Uri endpoint = OtlpSettings.ParseEndpoint(rawEndpoint, signal);
        var exporter = new OtlpExporterOptions();
        if (settings.ResolveProtocol() is { } protocol)
        {
            exporter.Protocol = protocol;
        }

        ApplyOtlpEndpoint(exporter, endpoint, signalPath);

        // Headers uses the OTLP environment convention ("k1=v1,k2=v2").
        string? headers = settings.HeadersFor(signal);
        if (!string.IsNullOrWhiteSpace(headers))
        {
            exporter.Headers = headers;
        }

        // Exporters create their own HttpClient; supply one that honours the
        // deployment TLS policy (see CreateHttpClientFactory).
        if (httpClientFactory is not null)
        {
            exporter.HttpClientFactory = httpClientFactory;
        }

        return exporter;
    }

    private static void CopyExporterOptions(OtlpExporterOptions target, OtlpExporterOptions source)
    {
        target.Endpoint = source.Endpoint;
        target.Protocol = source.Protocol;
        if (!string.IsNullOrWhiteSpace(source.Headers))
        {
            target.Headers = source.Headers;
        }
    }

    /// <summary>
    /// Assigning <see cref="OtlpExporterOptions.Endpoint"/> programmatically disables the SDK's
    /// signal-path appending, so with the http/protobuf protocol requests would go to the bare
    /// endpoint (for example <c>POST /</c>) and be rejected by spec-conformant collectors.
    /// Append the signal path ourselves when the endpoint carries none.
    ///
    /// A deployment may also configure the signal path explicitly (for example
    /// <c>http://masking-gateway:8102/v1/traces</c>); that path is kept verbatim
    /// rather than having the signal appended a second time.
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
