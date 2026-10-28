using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenAgent.Router.Middleware;

internal sealed record RequestBodySnapshot(
    bool IsOversized,
    bool IsJson,
    int Length,
    byte[] CanonicalBody,
    string Digest)
{
    private const string ItemKey = "RouterRequestBodySnapshot";

    internal static Task<RequestBodySnapshot> GetAsync(
        HttpContext context,
        int maxBodyBytes)
    {
        if (context.Items.TryGetValue(ItemKey, out object? value)
            && value is Task<RequestBodySnapshot> snapshotTask)
        {
            return snapshotTask;
        }

        Task<RequestBodySnapshot> created = ReadAsync(
            context.Request,
            maxBodyBytes,
            context.RequestAborted);
        context.Items[ItemKey] = created;
        return created;
    }

    internal static bool TryGet(
        HttpContext context,
        out Task<RequestBodySnapshot> snapshotTask)
    {
        if (context.Items.TryGetValue(ItemKey, out object? value)
            && value is Task<RequestBodySnapshot> existing)
        {
            snapshotTask = existing;
            return true;
        }

        snapshotTask = null!;
        return false;
    }

    internal static bool IsJsonContentType(HttpRequest request)
    {
        string? contentType = request.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        string mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RequestBodySnapshot> ReadAsync(
        HttpRequest request,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(request))
        {
            return Empty(isJson: false);
        }

        if (request.ContentLength > maxBodyBytes)
        {
            return new RequestBodySnapshot(true, true, checked((int)Math.Min(
                request.ContentLength.Value,
                int.MaxValue)), [], string.Empty);
        }

        request.EnableBuffering();
        byte[] rented = ArrayPool<byte>.Shared.Rent(81920);
        using MemoryStream body = new();
        try
        {
            while (body.Length <= maxBodyBytes)
            {
                int remaining = maxBodyBytes + 1 - checked((int)body.Length);
                int read = await request.Body.ReadAsync(
                    rented.AsMemory(0, Math.Min(rented.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await body.WriteAsync(
                    rented.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }

        if (body.Length > maxBodyBytes)
        {
            return new RequestBodySnapshot(true, true, checked((int)body.Length), [], string.Empty);
        }

        byte[] rawBody = body.ToArray();
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawBody);
            using MemoryStream canonical = new();
            using (Utf8JsonWriter writer = new(canonical))
            {
                WriteCanonical(writer, document.RootElement);
            }

            byte[] canonicalBody = canonical.ToArray();
            return new RequestBodySnapshot(
                false,
                true,
                rawBody.Length,
                canonicalBody,
                Convert.ToHexString(SHA256.HashData(canonicalBody)).ToLowerInvariant());
        }
        catch (JsonException)
        {
            return Empty(isJson: true) with { Length = rawBody.Length };
        }
    }

    private static RequestBodySnapshot Empty(bool isJson)
    {
        return new RequestBodySnapshot(false, isJson, 0, [], string.Empty);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind: {element.ValueKind}.");
        }
    }
}

internal static class RouterCacheKeyFactory
{
    internal static string GetRouteIdentity(HttpRequest request)
    {
        string query = string.Join(
            '&',
            request.Query
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? string.Empty)}")));
        string path = request.Path.Value?.ToLowerInvariant() ?? "/";
        return query.Length == 0
            ? $"{request.Method.ToUpperInvariant()}:{path}"
            : $"{request.Method.ToUpperInvariant()}:{path}?{query}";
    }

    internal static string GetRequestDigest(HttpRequest request, RequestBodySnapshot snapshot)
    {
        string material = string.Join(
            '\n',
            snapshot.Digest,
            request.Headers["X-Agent-Id"].FirstOrDefault() ?? string.Empty,
            request.Headers["X-Conversation-Id"].FirstOrDefault() ?? string.Empty);
        return Hash(material);
    }

    internal static string GetIdempotencyKey(
        string tenantId,
        string userId,
        string route,
        string clientKey)
    {
        return $"openagent:router:idempotency:v1:{Hash(string.Join('\n', tenantId, userId, route, clientKey))}";
    }

    internal static string GetQueryKey(
        string tenantId,
        string userId,
        string route,
        string requestDigest)
    {
        return $"openagent:router:query-cache:v1:{Hash(string.Join('\n', tenantId, userId, route, requestDigest))}";
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
