using OpenAgent.Contracts.Security;
using OpenAgent.Hosting;
using OpenAgent.Hosting.Authentication;
using OpenAgent.Router;
using OpenAgent.Router.Security;
using OpenAgent.Router.Middleware;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseAgentSerilog("agent-router");

builder.Services.AddAgentHost(builder.Configuration, options =>
{
    options.ServiceName = "agent-router";
    options.OpenTelemetrySource = "OpenAgent.Router";
});

builder.Services.AddHttpContextAccessor();

// IAgentUserContext is populated by JwtUserContextMiddleware (registered below in the pipeline).
// The DI factory retrieves the context that the middleware stored in HttpContext.Items.
builder.Services.AddScoped<IAgentUserContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    if (httpContext?.Items.TryGetValue("AgentUserContext", out var ctx) == true && ctx is IAgentUserContext userContext)
    {
        return userContext;
    }

    // Fallback for requests that bypass the middleware (e.g., health checks)
    return new AgentUserContext
    {
        UserId = "anonymous",
        TenantId = null,
        Groups = new List<string>(),
        Roles = new List<string>(),
        Claims = new Dictionary<string, string>(),
        Audience = new List<string> { "router" },
        IsAuthenticated = false
    };
});

builder.Services.AddRouterRuntime(builder.Configuration);
builder.Services.AddSingleton<IAgentVisibilityService, AgentVisibilityService>();
builder.Services.AddSingleton<IAgentAccessControl, AgentAccessControl>();

builder.Services.AddHttpForwarder();

builder.Services.AddHealthChecks()
    .AddCheck<RouterHealthCheck>("router", tags: new[] { "live" })
    .AddCheck<RouterReadyCheck>("router-ready", tags: new[] { "ready" });

var app = builder.Build();

app.UseAgentHost(builder.Configuration);
app.UseMiddleware<JwtUserContextMiddleware>();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/v1/agent/chat"),
    branch =>
    {
        branch.UseMiddleware<RateLimitingMiddleware>();
        branch.UseMiddleware<IdempotencyMiddleware>();
        branch.UseMiddleware<QueryCacheMiddleware>();
    });
app.MapControllers();
app.MapAgentAuthenticationEndpoints();
app.MapRouterEndpoints();

app.Run();
