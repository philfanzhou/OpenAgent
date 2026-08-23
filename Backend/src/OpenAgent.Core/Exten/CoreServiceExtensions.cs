using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using OpenAgent.Contracts.Configuration;

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
        services.Configure<AgentAuthorizationOptions>(configuration.GetSection("Authorization"));
        services.AddOptions<HumanApprovalOptions>()
            .Bind(configuration.GetSection(HumanApprovalOptions.SectionName))
            .Validate(
                options => options.RequestTimeoutMinutes > 0,
                "HumanApproval:RequestTimeoutMinutes must be greater than zero.")
            .Validate(
                options => options.SweepIntervalSeconds > 0,
                "HumanApproval:SweepIntervalSeconds must be greater than zero.");

        return services
            .AddConversationServices(configuration)
            .AddFileAssetServices(configuration)
            .AddCapabilityServices()
            .AddRuntimeServices();
    }
}
