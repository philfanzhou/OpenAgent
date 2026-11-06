using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Hosting.Authentication;
using OpenAgent.Engine.Hosting.Security;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenAgent.Engine.Hosting;

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
                    if (options.CorsAllowedOrigins.Contains("*", StringComparer.Ordinal))
                    {
                        policy.AllowAnyOrigin();
                    }
                    else
                    {
                        policy.WithOrigins(options.CorsAllowedOrigins).AllowCredentials();
                    }

                    policy
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .WithExposedHeaders("X-OpenAgent-Selected-Agent-Id")
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
            Func<HttpClient>? otlpHttpClientFactory = HttpClientSecurity.CreateHttpClientFactory(configuration);

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
                logs.AddOtlpExporter(exporter => ApplyOtlpExporter(
                    exporter, otlp, OtlpSignal.Logs, otlp.LogsEndpoint, otlpHttpClientFactory));
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

                tracing.AddOtlpExporter(exporter => ApplyOtlpExporter(
                    exporter, otlp, OtlpSignal.Traces, otlp.TracesEndpoint, otlpHttpClientFactory));
            });

            openTelemetry.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(options.OpenTelemetrySource)
                    .AddPrometheusExporter();

                metrics.AddOtlpExporter(exporter => ApplyOtlpExporter(
                    exporter, otlp, OtlpSignal.Metrics, otlp.MetricsEndpoint, otlpHttpClientFactory));
            });
        }

        return services;
    }

    /// <summary>
    /// Configures one signal's exporter, or disables it when that signal has no endpoint.
    /// </summary>
    /// <remarks>
    /// Each signal is resolved independently so traces can reach the audit gateway
    /// while logs stay local. Setting <see cref="OtlpExporterOptions.Endpoint"/>
    /// programmatically also disables the SDK's signal-path appending, so with
    /// http/protobuf the path is appended here — otherwise every request goes to
    /// <c>POST /</c> and a spec-conformant collector rejects it.
    /// </remarks>
    internal static void ApplyOtlpExporter(
        OtlpExporterOptions exporter,
        OtlpSettings settings,
        OtlpSignal signal,
        Uri? endpoint,
        Func<HttpClient>? httpClientFactory = null)
    {
        if (endpoint is null)
        {
            // No endpoint for this signal: leave the exporter's own defaults alone
            // but do not point it at a collector that does not exist. The caller
            // registers the exporter unconditionally (the SDK requires it), so the
            // "disabled" state is expressed by keeping the configured endpoint null.
            ApplyProtocol(exporter, settings.Protocol);
            return;
        }

        ApplyProtocol(exporter, settings.Protocol);
        ApplyHeaders(exporter, settings.HeadersFor(signal));

        exporter.Endpoint = endpoint;
        if (exporter.Protocol == OtlpExportProtocol.HttpProtobuf && endpoint.AbsolutePath == "/")
        {
            exporter.Endpoint = new UriBuilder(endpoint) { Path = $"/{settings.PathFor(signal)}" }.Uri;
        }

        if (httpClientFactory is not null)
        {
            // Without this the exporter builds its own client and ignores the
            // deployment TLS policy, so a self-signed collector silently drops
            // every batch.
            exporter.HttpClientFactory = httpClientFactory;
        }
    }

    private static void ApplyProtocol(OtlpExporterOptions exporter, string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return;
        }

        exporter.Protocol = protocol.Trim().ToLowerInvariant() switch
        {
            "http/protobuf" or "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
            "grpc" => OtlpExportProtocol.Grpc,
            _ => throw new InvalidOperationException(
                $"OpenTelemetry:Protocol must be 'http/protobuf' or 'grpc'. Value: '{protocol}'.")
        };
    }

    private static void ApplyHeaders(OtlpExporterOptions exporter, IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0)
        {
            return;
        }

        var target = exporter.Headers ?? string.Empty;
        IEnumerable<string> existing = target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<string> merged = existing
            .Concat(headers.Select(pair => $"{pair.Key}={pair.Value}"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        exporter.Headers = string.Join(",", merged);
    }
}
