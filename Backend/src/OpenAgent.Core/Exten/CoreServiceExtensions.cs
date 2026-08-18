using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Exten;

public static class CoreServiceExtensions
{
    public static IServiceCollection AddAgentCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton<IConfiguration>(configuration);
        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });
        });
        services.AddHttpContextAccessor();
        services.Configure<McpExecutionOptions>(configuration.GetSection("Mcp"));
        services.Configure<SkillScriptSandboxOptions>(
            configuration.GetSection(SkillScriptSandboxOptions.SectionName));
        services.Configure<AgentAuthorizationOptions>(configuration.GetSection("Authorization"));
        services.TryAddSingleton<ISkillScriptSandbox>(serviceProvider =>
        {
            SkillScriptSandboxOptions options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<SkillScriptSandboxOptions>>()
                .Value;
            return options.Enabled
                ? ActivatorUtilities.CreateInstance<HttpSkillScriptSandbox>(serviceProvider)
                : new DisabledSkillScriptSandbox();
        });

        return services
            .AddConversationServices(configuration)
            .AddFileAssetServices(configuration)
            .AddCapabilityServices()
            .AddRuntimeServices();
    }
}
