using System.Security.Claims;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Security;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Security;

public class JwtUserContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JwtUserContextMiddleware> _logger;
    private readonly bool _tenantEnabled;

    public JwtUserContextMiddleware(
        RequestDelegate next,
        ILogger<JwtUserContextMiddleware> logger,
        IHostEnvironment environment,
        IOptions<AgentAuthenticationOptions> authenticationOptions)
    {
        _next = next;
        _logger = logger;
        _tenantEnabled = authenticationOptions.Value.EnableTenant;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        IAgentUserContext userContext;

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value
                ?? "unknown";

            string? tenantId = _tenantEnabled
                ? TenantIdentityResolver.ResolveClaimsOnly(context.User)
                : null;

            if (_tenantEnabled
                && RequiresTenant(context.Request.Path)
                && string.IsNullOrWhiteSpace(tenantId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var roles = context.User.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "roles")
                .Select(c => c.Value)
                .ToList();

            var groups = context.User.Claims
                .Where(c => c.Type == "groups" || c.Type == "group")
                .Select(c => c.Value)
                .ToList();

            var claims = context.User.Claims
                .GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => string.Join(",", g.Select(c => c.Value)));

            var audience = context.User.Claims
                .Where(c => c.Type == "aud")
                .Select(c => c.Value)
                .ToList();

            userContext = new AgentUserContext
            {
                UserId = userId,
                TenantId = tenantId,
                Groups = groups,
                Roles = roles,
                Claims = claims,
                Audience = audience,
                IsAuthenticated = true
            };

            RouterLog.AuthenticatedUserContextCreated(_logger, userId, tenantId, roles.Count, groups.Count, audience.Count);
        }
        else
        {
            userContext = new AgentUserContext
            {
                UserId = "anonymous",
                TenantId = null,
                Groups = new List<string>(),
                Roles = new List<string>(),
                Claims = new Dictionary<string, string>(),
                Audience = new List<string> { "router" },
                IsAuthenticated = false
            };

            RouterLog.AnonymousUserContextCreated(_logger, context.Request.Path, context.TraceIdentifier);
        }

        context.Items["AgentUserContext"] = userContext;

        await _next(context);
    }

    private static bool RequiresTenant(PathString path) =>
        path.StartsWithSegments("/api/v1/agent")
        || path.StartsWithSegments("/api/v1/admin");
}
