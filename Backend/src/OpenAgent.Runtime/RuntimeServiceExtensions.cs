using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Runtime;

public static class RuntimeServiceExtensions
{
    public static IServiceCollection AddOpenCodeRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();
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
        services.TryAddSingleton<IAgentRuntimeDriver, OpenCodeRuntime>();
        return services;
    }
}
