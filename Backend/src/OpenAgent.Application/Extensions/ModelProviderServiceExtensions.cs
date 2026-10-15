using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Application.ModelProviders;

namespace OpenAgent.Application.Extensions;

internal static class ModelProviderServiceExtensions
{
    internal static IServiceCollection AddModelProviderServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmInteractionOptions>(configuration.GetSection(LlmInteractionOptions.SectionName));
        services.TryAddSingleton<IAgentChatClientFactory, AgentChatClientFactory>();
        return services;
    }
}
