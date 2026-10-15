using OpenAgent.Contracts.Capabilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Capabilities.Files.Multimodal;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Capabilities.Files.Sharing;
using OpenAgent.Capabilities.Files.Tools;

namespace OpenAgent.Capabilities.Extensions;

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
        services.AddSingleton<IValidateOptions<FileShareOptions>, FileShareOptionsValidator>();
        services.AddOptions<FileShareOptions>()
            .Bind(configuration.GetSection(FileShareOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IFileObjectStore, UnconfiguredFileObjectStore>();
        services.TryAddSingleton<IFileShareRepository, UnconfiguredFileShareRepository>();
        services.TryAddSingleton<IInlineImageOptimizer, InlineImageOptimizer>();
        services.AddScoped<IFileAssetService, FileAssetService>();
        services.AddScoped<IFileShareService, FileShareService>();
        services.AddScoped<FileAssetExecutionContext>();
        services.AddScoped<FileAssetRequestResolver>();
        bool allowInsecureTls = configuration.GetValue("OPENAGENT_ALLOW_INSECURE_TLS", false);
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
