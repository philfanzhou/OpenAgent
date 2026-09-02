using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.Router.Observability;

internal static class RouterHttpLog
{
    private const int MaxHeaderValueLength = 512;
    private const int MaxBodyLength = 16 * 1024;

    internal static string FormatRequestHeaders(HttpRequestMessage request)
    {
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = request.Headers;
        if (request.Content != null)
        {
            headers = headers.Concat(request.Content.Headers);
        }

        return FormatHeaders(headers);
    }

    internal static string FormatResponseHeaders(HttpResponseMessage response)
    {
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = response.Headers;
        if (response.Content != null)
        {
            headers = headers.Concat(response.Content.Headers);
        }

        return FormatHeaders(headers);
    }

    internal static string FormatResponseHeaders(IHeaderDictionary headers) =>
        FormatHeaders(headers.Select(header =>
            new KeyValuePair<string, IEnumerable<string>>(
                header.Key,
                header.Value.Select(value => value ?? string.Empty).ToArray())));

    internal static string FormatBody(string body)
    {
        string sanitized = body
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return sanitized.Length <= MaxBodyLength
            ? sanitized
            : $"{sanitized[..MaxBodyLength]}... [truncated]";
    }

    private static string FormatHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
        string.Join(
            "; ",
            headers
                .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .Select(header =>
                    $"{header.Key}={string.Join(",", header.Value.Select(value => FormatHeaderValue(header.Key, value)))}"));

    private static string FormatHeaderValue(string name, string value)
    {
        if (IsSensitiveHeader(name))
        {
            return "[REDACTED]";
        }

        string sanitized = value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return sanitized.Length <= MaxHeaderValueLength
            ? sanitized
            : $"{sanitized[..MaxHeaderValueLength]}... [truncated]";
    }

    private static bool IsSensitiveHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase);
}
