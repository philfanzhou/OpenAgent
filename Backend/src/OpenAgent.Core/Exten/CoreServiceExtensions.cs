using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Contracts.Execution;

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
        services.AddOptions<CodeExecutionOptions>().Bind(configuration.GetSection("CodeExecution"))
            .Validate(options => !options.Enabled ||
                (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
                    && options.ApiKey.Length >= 32 && options.RequestTimeoutSeconds is >= 10 and <= 900
                    && options.MaxExecutionsPerRequest is >= 1 and <= 32),
                "CodeExecution requires an HTTP(S) Runner endpoint, a 32-character API key, and a bounded timeout.")
            .ValidateOnStart();
        services.AddHttpClient<ICodeExecutor, RunnerClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<OpenAgent.Core.Capabilities.ICapabilitySource, CodeCapabilitySource>();

        return services
            .AddConversationServices(configuration)
            .AddFileAssetServices(configuration)
            .AddCapabilityServices()
            .AddRuntimeServices();
    }
}
