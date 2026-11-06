using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using Microsoft.Extensions.Configuration;

namespace OpenAgent.Hosting;

/// <summary>
/// Resolves the deployment-wide outbound TLS policy and applies it to HTTP handlers.
/// Certificate validation remains enabled unless the explicit deployment switch is set.
/// </summary>
public static class HttpClientSecurity
{
    /// <summary>
    /// Whether the deployment opted into accepting certificates it does not
    /// otherwise trust.
    ///
    /// Parsed leniently: the binder's <c>GetValue&lt;bool&gt;</c> throws on
    /// <c>1</c>/<c>0</c>/empty strings, which are exactly the values compose
    /// files and shell scripts pass, and a throw here would fail service
    /// startup over a telemetry switch.
    /// </summary>
    public static bool AllowInsecureTls(IConfiguration configuration)
    {
        string? raw = configuration["OPENAGENT_ALLOW_INSECURE_TLS"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => false
        };
    }

    /// <summary>
    /// Builds the factory used by components that own their HTTP stack (the
    /// OTLP exporters do). Returning <c>null</c> leaves the component's own
    /// default in place — the SDK default already validates certificates, so
    /// the factory is only needed when the deployment opts into accepting an
    /// otherwise-untrusted (typically internal, self-signed) CA.
    ///
    /// Without this hook, a deployment that trusts an internal CA everywhere
    /// else would still drop every trace at the collector, because the
    /// exporter never consults <c>OPENAGENT_ALLOW_INSECURE_TLS</c>.
    /// </summary>
    public static Func<HttpClient>? CreateHttpClientFactory(IConfiguration configuration)
    {
        if (!AllowInsecureTls(configuration))
        {
            return null;
        }

        // One handler per factory invocation: the exporter may create several
        // clients, and a shared handler that gets disposed breaks the others.
        return () => new HttpClient(
            CreateHttpClientHandler(configuration, allowInsecureTls: true),
            disposeHandler: true);
    }

    public static HttpClientHandler CreateHttpClientHandler(
        IConfiguration configuration,
        bool allowAutoRedirect = true,
        bool useProxy = true,
        bool? allowInsecureTls = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            UseProxy = useProxy
        };
        if (allowInsecureTls ?? AllowInsecureTls(configuration))
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    public static SocketsHttpHandler CreateSocketsHttpHandler(
        IConfiguration configuration,
        TimeSpan connectTimeout,
        bool? allowInsecureTls = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = DistributedContextPropagator.Current,
            ConnectTimeout = connectTimeout
        };
        if (allowInsecureTls ?? AllowInsecureTls(configuration))
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            };
        }

        return handler;
    }
}
