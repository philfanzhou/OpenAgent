using System.Text.Json;

namespace OpenAgent.Router.Middleware;

internal static class RouterCachePolicy
{
    internal static bool IsStreamingRequest(HttpRequest request)
    {
        string? action = request.HttpContext.Request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return string.Equals(action, "stream", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "sse", StringComparison.OrdinalIgnoreCase)
            || request.Headers.Accept.Any(value =>
                value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static bool IsStreamingContentType(string? contentType)
    {
        return contentType?.StartsWith(
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase) == true
            || contentType?.StartsWith(
                "application/x-ndjson",
                StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool IsSuccessful(int statusCode) => statusCode is >= 200 and < 300;

    internal static bool IsQueryRequestCacheable(
        HttpRequest request,
        RequestBodySnapshot snapshot)
    {
        if (snapshot.IsOversized
            || snapshot.Digest.Length == 0
            || request.Headers["X-Conversation-Id"].Count > 0
            || HasDirective(request.Headers.CacheControl, "no-store")
            || HasDirective(request.Headers.CacheControl, "no-cache"))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(snapshot.CanonicalBody);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("fileIds", out JsonElement fileIds)
            && fileIds.ValueKind == JsonValueKind.Array
            && fileIds.GetArrayLength() > 0)
        {
            return false;
        }

        if (!root.TryGetProperty("context", out JsonElement context)
            || context.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (context.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in context.EnumerateObject())
        {
            if (!property.Name.Equals("agentId", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsQueryResponseCacheable(HttpResponse response)
    {
        if (!IsSuccessful(response.StatusCode)
            || IsStreamingContentType(response.ContentType)
            || response.ContentType?.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase) != true
            || response.Headers.SetCookie.Count > 0
            || !HasDirective(response.Headers.CacheControl, "public")
            || HasDirective(response.Headers.CacheControl, "private")
            || HasDirective(response.Headers.CacheControl, "no-store")
            || HasDirective(response.Headers.CacheControl, "no-cache"))
        {
            return false;
        }

        string vary = response.Headers.Vary.ToString();
        return !vary.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            && !vary.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            && !vary.Contains("x-user-id", StringComparison.OrdinalIgnoreCase)
            && !vary.Contains("x-conversation-id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDirective(string? values, string directive)
    {
        if (string.IsNullOrWhiteSpace(values))
        {
            return false;
        }

        return values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Equals(directive, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(directive + "=", StringComparison.OrdinalIgnoreCase));
    }
}
