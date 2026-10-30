using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Admin;

/// <summary>Configuration mutation is privileged; catalog readers do not acquire write access.</summary>
internal sealed class ConfigurationWriteFilter(IHostEnvironment environment, IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        bool developmentCompatibility = environment.IsDevelopment()
            && string.Equals(configuration["Authorization:Mode"], "AllowAll", StringComparison.OrdinalIgnoreCase)
            && !configuration.GetValue<bool>("Authorization:RequireResourcePolicy")
            && http.GetAgentRequest().User.IsAuthenticated;
        if (!developmentCompatibility && !HttpMethods.IsGet(http.Request.Method)
            && !ResourceAccess.HasScope(http.GetAgentRequest().User, "agent.config.write"))
            return TypedResults.Problem(AgentProblemDetails.PermissionDenied("Configuration write permission is required.", http));
        return await next(context).ConfigureAwait(false);
    }
}
