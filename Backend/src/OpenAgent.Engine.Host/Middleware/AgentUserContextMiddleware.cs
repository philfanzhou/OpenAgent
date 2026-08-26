using System.Security.Claims;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Security;

namespace OpenAgent.Engine.Host.Middleware;

internal sealed class AgentUserContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgentUserContextMiddleware> _logger;
    public AgentUserContextMiddleware(
        RequestDelegate next,
        ILogger<AgentUserContextMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string traceId = TraceIdResolver.Resolve(context);
        AgentUserContext user = BuildUserContext(context);
        if (RequiresTenant(context.Request.Path)
            && user.IsAuthenticated
            && string.IsNullOrWhiteSpace(user.TenantId))
        {
            throw new TenantDataIsolationException(
                null,
                null,
                "TenantId is required but not provided");
        }
        context.Features.Set(new AgentRequestFeature(traceId, user));

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["TenantId"] = user.TenantId,
            ["UserId"] = user.UserId
        });
        await _next(context).ConfigureAwait(false);
    }

    private AgentUserContext BuildUserContext(HttpContext context)
    {
        string? userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.Identity?.Name;
        string? tenantId = TenantIdentityResolver.Resolve(context.User, context.Request.Headers);
        List<string> roles = context.User.Claims
            .Where(claim => claim.Type == ClaimTypes.Role || claim.Type is "roles" or "role")
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> groups = context.User.Claims
            .Where(claim => claim.Type is "groups" or "group")
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Dictionary<string, string> claims = context.User.Claims
            .GroupBy(claim => claim.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(",", group.Select(claim => claim.Value)),
                StringComparer.OrdinalIgnoreCase);

        return new AgentUserContext
        {
            UserId = userId ?? "anonymous",
            TenantId = tenantId,
            Groups = groups.AsReadOnly(),
            Roles = roles.AsReadOnly(),
            Claims = claims.AsReadOnly(),
            Audience = ResolveAudience(context),
            IsAuthenticated = context.User.Identity?.IsAuthenticated ?? false
        };
    }

    private static IReadOnlyList<string> ResolveAudience(HttpContext context)
    {
        if (context.Items.TryGetValue("Audience", out object? audience)
            && audience is IEnumerable<string> values)
        {
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }

        return context.Request.Headers["X-Agent-Audience"]
            .SelectMany(value => (value ?? string.Empty).Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static bool RequiresTenant(PathString path) =>
        path.StartsWithSegments("/api/v1/agent")
        || path.StartsWithSegments("/api/v1/admin");
}

internal sealed record AgentRequestFeature(
    string TraceId,
    IAgentUserContext User);

internal static class AgentRequestFeatureExtensions
{
    internal static AgentRequestFeature GetAgentRequest(this HttpContext context) =>
        context.Features.Get<AgentRequestFeature>()
        ?? throw new InvalidOperationException("Agent request middleware has not run.");
}
