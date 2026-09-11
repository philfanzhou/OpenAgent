using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities;

namespace OpenAgent.Core.Files;

internal static class FileAssetServiceExtensions
{
    internal static IServiceCollection AddFileAssetServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<FileAssetOptions>, FileAssetOptionsValidator>();
        services.AddOptions<FileAssetOptions>()
            .Bind(configuration.GetSection(FileAssetOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IFileObjectStore, UnconfiguredFileObjectStore>();
        services.AddScoped<IFileAssetService, FileAssetService>();
        services.AddScoped<FileAssetExecutionContext>();
        services.AddScoped<FileAssetRequestResolver>();
        bool allowInsecureTls = configuration.GetValue("OPENAGENT_ALLOW_INSECURE_TLS", false)
            || configuration.GetValue("Http:AllowInsecureTls", false);
        services.AddHttpClient("AgentFileDownload", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(FileAssetUrlDownloader.DefaultTimeoutSeconds);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAgent-FileDownloader/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false
                };
                if (allowInsecureTls)
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                return handler;
            });
        services.AddScoped<FileAssetUrlDownloader>();
        services.AddScoped<ICapabilitySource, FileAssetCapabilitySource>();
        return services;
    }
}
