using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Hosting.Errors;

namespace OpenAgent.Hosting;

public static class AgentErrorServiceCollectionExtensions
{
    /// <summary>
    /// 注册共享的异常映射与全局异常处理中间件设施。configure 可注入服务特定行为
    /// （额外异常映射、SSE 错误文案、流式端点判定）。
    /// </summary>
    public static IServiceCollection AddAgentErrorHandling(
        this IServiceCollection services,
        Action<AgentExceptionHandlingOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }
        services.TryAddSingleton<AgentExceptionMapper>();
        return services;
    }

    /// <summary>把共享的全局异常处理中间件加入管道；应放在认证之后、端点映射之前。</summary>
    public static IApplicationBuilder UseAgentErrorHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<AgentExceptionHandlerMiddleware>();
}
