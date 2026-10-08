using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Execution;
using OpenAgent.Core.Integrations.Runner;

namespace OpenAgent.Core.Extensions;

internal static class RunnerServiceExtensions
{
    internal static IServiceCollection AddRunnerServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CodeExecutionOptions>().Bind(configuration.GetSection("CodeExecution"))
            .Validate(options => !options.Enabled ||
                (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
                    && options.ApiKey.Length >= 32 && options.RequestTimeoutSeconds is >= 10 and <= 900),
                "CodeExecution requires an HTTP(S) Runner endpoint, a 32-character API key, and a bounded timeout.")
            .ValidateOnStart();
        bool allowInsecureTls = configuration.GetValue("OPENAGENT_ALLOW_INSECURE_TLS", false);
        services.AddHttpClient<RunnerClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                if (allowInsecureTls)
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
                return handler;
            });
        services.TryAddScoped<ICodeExecutor>(provider => provider.GetRequiredService<RunnerClient>());
        services.TryAddScoped<IWorkspaceClient>(provider => provider.GetRequiredService<RunnerClient>());
        return services;
    }
}
