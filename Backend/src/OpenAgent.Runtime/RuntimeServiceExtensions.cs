using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.OpenCode;
using OpenAgent.Runtime.Pi;

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
                // 归一化原生工具白名单：去空白、去重；结构性校验（禁花括号）在 Validate。
                options.AllowedNativeTools = options.AllowedNativeTools
                    .Where(tool => !string.IsNullOrWhiteSpace(tool))
                    .Select(tool => tool.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.Executable)
                && Path.IsPathFullyQualified(options.WorkspaceRoot)
                && Path.IsPathFullyQualified(options.SessionStateRoot)
                && options.Arguments is not null
                && options.RequestTimeoutSeconds is > 0 and <= 3600
                && options.TurnTimeoutSeconds is > 0 and <= 86400
                && options.MaxConcurrentTurns is > 0 and <= 128
                && options.MaxPromptCharacters > 0
                && options.KeepAliveSeconds is >= 0 and <= 86400
                && options.MaxKeptProcesses is > 0 and <= 128
                // 共享存储下，另一实例的空闲保活进程不能比它的会话状态活得久。
                && options.SessionStateRetentionDays is >= 0 and <= 3650
                && (options.SessionStateRetentionDays == 0 || options.SessionStateRetentionDays * 86400L > options.KeepAliveSeconds)
                && options.MaxSessionsPerProcess is > 0 and <= 10000
                && options.AllowedNativeTools is not null
                // OpenCode 在 JSON 解析前展开 {file:...}/{env:...} 宏，白名单条目带花括号
                // 会变成宿主文件/环境读取，必须拒绝。
                && options.AllowedNativeTools.All(tool => !tool.Contains('{') && !tool.Contains('}')),
                "Invalid OpenCode runtime configuration.")
            .ValidateOnStart();
        services.TryAddSingleton<RuntimeToolBridge>();
        services.TryAddSingleton<OpenCodeProcessKeeper>();
        // 会话状态回收随宿主启动；库与测试组合不启动宿主服务，直接调用 SweepOnceAsync。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OpenCodeSessionStateSweeper>());
        services.TryAddSingleton<IAgentRuntimeDriver, OpenCodeRuntime>();
        PiOptions pi = configuration.GetSection("Pi").Get<PiOptions>() ?? new PiOptions();
        if (pi.Enabled)
        {
            if (!Path.IsPathFullyQualified(pi.Executable) || pi.AllowedTenants.Length == 0)
                throw new InvalidOperationException("Pi requires an absolute executable path and an explicit tenant allowlist.");
            services.TryAddSingleton(pi);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntimeDriver, PiRuntime>());
            services.TryAddSingleton<IConfiguredRuntimeProfiles, PiProfiles>();
        }
        return services;
    }

    /// <summary>把工具桥挂到主站已有端口；须在用户上下文/准入等中间件之前调用。</summary>
    public static void MapRuntimeToolBridge(this WebApplication app)
    {
        app.Services.GetRequiredService<RuntimeToolBridge>().Mount(app);
    }
}
