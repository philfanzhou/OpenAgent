using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files;

internal sealed record DownloadedFile(
    string FileName,
    string MediaType,
    byte[] Content);

/// <summary>
/// Downloads an explicitly supplied public HTTP(S) resource with bounded redirects and content size.
/// </summary>
internal sealed class FileAssetUrlDownloader
{
    internal const int DefaultTimeoutSeconds = 30;
    private const int MaxRedirects = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FileAssetOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHostAddresses;

    public FileAssetUrlDownloader(
        IHttpClientFactory httpClientFactory,
        IOptions<FileAssetOptions> options)
        : this(httpClientFactory, options, ResolveHostAddressesAsync)
    {
    }

    internal FileAssetUrlDownloader(
        IHttpClientFactory httpClientFactory,
        IOptions<FileAssetOptions> options,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAddresses)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _resolveHostAddresses = resolveHostAddresses;
    }

    internal async Task<DownloadedFile> DownloadAsync(
        string address,
        CancellationToken cancellationToken)
    {
        Uri current = await ValidateAddressAsync(address, cancellationToken).ConfigureAwait(false);
        HttpClient client = _httpClientFactory.CreateClient("AgentFileDownload");

        for (int redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= MaxRedirects)
                {
                    throw new AgentException(
                        AgentErrorCode.InvalidRequest,
                        "下载地址的重定向次数超过限制。");
                }

                Uri? location = response.Headers.Location;
                if (location == null)
                {
                    throw new AgentException(
                        AgentErrorCode.InvalidRequest,
                        "下载地址返回了无效的重定向响应。");
                }

                current = await ValidateAddressAsync(
                    new Uri(current, location).ToString(),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AgentException(
                    AgentErrorCode.DependencyUnavailable,
                    $"下载地址返回 HTTP {(int)response.StatusCode}。");
            }

            if (response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength > _options.MaxFileSizeBytes)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "远程文件超过当前会话的文件大小限制。");
            }

            string fileName = ResolveFileName(response, current);
            string mediaType = ResolveMediaType(response.Content.Headers.ContentType?.MediaType, fileName);
            byte[] content = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return new DownloadedFile(fileName, mediaType, content);
        }
    }

    private async Task<Uri> ValidateAddressAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "只支持不带账号密码的 HTTP(S) 下载地址。");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolveHostAddresses(uri.DnsSafeHost, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "下载地址的主机无法解析。",
                    innerException: exception);
            }
        }

        if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "出于安全原因，不允许下载内网或本机地址。");
        }

        return uri;
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(
        string host,
        CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    private async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length > _options.MaxFileSizeBytes - read)
            {
                throw new AgentException(
                    AgentErrorCode.InvalidRequest,
                    "远程文件超过当前会话的文件大小限制。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (output.Length == 0)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "远程文件内容为空。");
        }

        return output.ToArray();
    }

    private static string ResolveFileName(HttpResponseMessage response, Uri address)
    {
        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        string? candidate = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Uri.UnescapeDataString(address.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty);
        }

        candidate = Path.GetFileName(candidate.Trim().Trim('"').Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..")
        {
            candidate = "download";
        }

        string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(candidate))
            && MediaTypeToExtension.TryGetValue(mediaType, out string? extension))
        {
            candidate += extension;
        }

        return candidate;
    }

    private static string ResolveMediaType(string? mediaType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(mediaType)
            && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return mediaType;
        }

        return MediaTypeExtensions.TryGetValue(Path.GetExtension(fileName), out string? inferred)
            ? inferred
            : mediaType ?? "application/octet-stream";
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || bytes[0] >= 224;
        }

        return (bytes[0] & 0xFE) == 0xFC;
    }

    private static readonly IReadOnlyDictionary<string, string> MediaTypeExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",
            [".pdf"] = "application/pdf",
            [".json"] = "application/json",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".md"] = "text/markdown",
            [".html"] = "text/html",
            [".htm"] = "text/html"
        };

    private static readonly IReadOnlyDictionary<string, string> MediaTypeToExtension =
        MediaTypeExtensions
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
            group => group.Key,
            group => group.First().Key,
            StringComparer.OrdinalIgnoreCase);
}
