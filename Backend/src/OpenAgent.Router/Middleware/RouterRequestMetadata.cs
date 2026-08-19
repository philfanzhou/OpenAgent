namespace OpenAgent.Router.Middleware;

internal static class RouterRequestMetadata
{
    internal static string? GetAction(HttpContext context)
    {
        const string prefix = "/api/v1/agent/chat/";
        string? path = context.Request.Path.Value;
        return path?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? path[prefix.Length..]
            : null;
    }
}
