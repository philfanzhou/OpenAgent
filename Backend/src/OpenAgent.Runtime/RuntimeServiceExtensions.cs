using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.OpenCode;

namespace OpenAgent.Runtime;

public static class RuntimeServiceExtensions
{
    public static IServiceCollection AddOpenCodeRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();
        // MCP server 服务供挂载路由解析；未挂载的组合（库/测试）不会触达这些注册。
        RuntimeToolBridge.AddBridgeServer(services);
        services.AddOptions<OpenCodeOptions>()
            .Bind(configuration.GetSection(OpenCodeOptions.SectionName))
            .PostConfigure(options =>
            {
                if (options.Arguments is { Length: 0 })
                {
                    options.Arguments = ["acp"];
                }
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.Executable)
                && Path.IsPathFullyQualified(options.WorkspaceRoot)
                && options.Arguments is not null
                && options.RequestTimeoutSeconds is > 0 and <= 3600
                && options.TurnTimeoutSeconds is > 0 and <= 86400
                && options.MaxConcurrentTurns is > 0 and <= 128
                && options.MaxPromptCharacters > 0, "Invalid OpenCode runtime configuration.")
            .ValidateOnStart();
        services.TryAddSingleton<RuntimeToolBridge>();
        services.TryAddSingleton<IAgentRuntimeDriver, OpenCodeRuntime>();
        return services;
    }

    /// <summary>把工具桥挂到主站已有端口；须在用户上下文/准入等中间件之前调用。</summary>
    public static void MapRuntimeToolBridge(this WebApplication app)
    {
        app.Services.GetRequiredService<RuntimeToolBridge>().Mount(app);
    }
}
