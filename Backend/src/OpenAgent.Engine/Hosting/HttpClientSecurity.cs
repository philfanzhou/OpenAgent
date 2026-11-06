using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using Microsoft.Extensions.Configuration;

namespace OpenAgent.Engine.Hosting;

/// <summary>
/// Resolves the deployment-wide outbound TLS policy and applies it to HTTP handlers.
/// Certificate validation remains enabled unless the explicit deployment switch is set.
/// </summary>
public static class HttpClientSecurity
{
    /// <summary>
    /// Reads the deployment-wide TLS switch leniently.
    /// </summary>
    /// <remarks>
    /// <c>GetValue&lt;bool&gt;</c> throws for values deployments actually use —
    /// <c>"1"</c> from a shell script, or an empty string from a compose file that
    /// left the variable blank. Both mean "not set"; treating them as an error
    /// takes the whole service down over a configuration nicety.
    /// </remarks>
    public static bool AllowInsecureTls(IConfiguration configuration)
    {
        string? raw = configuration["OPENAGENT_ALLOW_INSECURE_TLS"]
            ?? Environment.GetEnvironmentVariable("OPENAGENT_ALLOW_INSECURE_TLS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"OPENAGENT_ALLOW_INSECURE_TLS must be true/false (or 1/0). Value: '{raw}'.")
        };
    }

    /// <summary>
    /// Builds a client factory for exporters that must honour the same TLS policy as
    /// the rest of the platform.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when certificate verification is on, so the
    /// exporter keeps its default handler. The OTLP exporters otherwise build their
    /// own client and ignore the deployment switch entirely: against a self-signed
    /// collector every batch fails silently and the audit plane never sees the data.
    ///
    /// A fresh handler is created per call because a handler cannot be reused after
    /// it has been disposed.
    /// </remarks>
    public static Func<HttpClient>? CreateHttpClientFactory(IConfiguration configuration)
    {
        if (!AllowInsecureTls(configuration))
        {
            return null;
        }

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
