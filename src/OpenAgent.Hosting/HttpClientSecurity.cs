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
    public static bool AllowInsecureTls(IConfiguration configuration) =>
        configuration.GetValue("OPENAGENT_ALLOW_INSECURE_TLS", false);

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
