using System.Security.Claims;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Security;

public class JwtUserContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JwtUserContextMiddleware> _logger;

    public JwtUserContextMiddleware(RequestDelegate next, ILogger<JwtUserContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        IAgentUserContext userContext;

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value
                ?? "unknown";

            var tenantId = context.User.FindFirst("tid")?.Value
                ?? context.User.FindFirst("tenant_id")?.Value;

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
}
