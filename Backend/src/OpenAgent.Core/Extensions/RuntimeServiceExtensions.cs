using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Files.Requests;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Extensions;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<AgentRuntimeResolver>();
        services.TryAddScoped<IAgentRuntimeResolver>(provider => provider.GetRequiredService<AgentRuntimeResolver>());
        services.TryAddScoped<AgentFactory>();
        services.AddOptions<AcpOptions>().Bind(configuration.GetSection("AgentRuntimes:Acp"))
            .Validate(options => !options.Enabled || (options.TurnTimeoutSeconds is > 0 and <= 3600
                && options.RequestTimeoutSeconds is > 0 and <= 300
                && Path.IsPathFullyQualified(options.WorkspaceRoot)
                && options.Harnesses.Select(harness => harness.Id).Distinct(StringComparer.Ordinal).Count() == options.Harnesses.Count
                && options.Harnesses.All(harness => !string.IsNullOrWhiteSpace(harness.Id)
                    && !harness.Id.Contains(':') && !string.IsNullOrWhiteSpace(harness.Command)
                    && (harness.EnvironmentMode == "isolated" || (harness.EnvironmentMode == "local" && options.LocalTrusted))
                    && (harness.WorkingDirectory == null || (options.LocalTrusted && Path.IsPathFullyQualified(harness.WorkingDirectory)))
                    && (!harness.UsePlatformModel || harness.ModelEnvironmentVariable == null)
                    && harness.PermissionPolicy is "deny" or "allow_once")), "Invalid ACP runtime configuration.");
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRuntimeAdapter, MafRuntimeAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRuntimeAdapter, AcpRuntimeAdapter>());
        services.TryAddScoped<RuntimeRegistry>();
        services.TryAddScoped<IRuntimeCatalog>(provider => provider.GetRequiredService<RuntimeRegistry>());
        services.TryAddScoped<IRuntimeDiagnostics, AcpRuntimeDiagnostics>();
        services.TryAddScoped(provider => new AgentExecutor(
            provider.GetRequiredService<IAgentRuntimeResolver>(),
            provider.GetRequiredService<AgentFactory>(),
            provider.GetRequiredService<ConversationAgentResolver>(),
            provider.GetRequiredService<FileAssetRequestResolver>(),
            provider.GetRequiredService<RuntimeRegistry>(),
            provider.GetRequiredService<IConversationStore>()));
        return services;
    }
}
