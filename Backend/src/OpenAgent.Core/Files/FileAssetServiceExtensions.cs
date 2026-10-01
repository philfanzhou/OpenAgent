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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FileAssetOptions>, FileAssetOptionsValidator>());
        services.AddOptions<FileAssetOptions>()
            .Bind(configuration.GetSection(FileAssetOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FileShareOptions>, FileShareOptionsValidator>());
        services.AddOptions<FileShareOptions>()
            .Bind(configuration.GetSection(FileShareOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IFileObjectStore, UnconfiguredFileObjectStore>();
        services.TryAddSingleton<IFileShareRepository, UnconfiguredFileShareRepository>();
        services.TryAddSingleton<IInlineImageOptimizer, InlineImageOptimizer>();
        services.TryAddScoped<IFileAssetService, FileAssetService>();
        services.TryAddScoped<IFileShareService, FileShareService>();
        services.TryAddScoped<FileAssetExecutionContext>();
        services.TryAddScoped<FileAssetRequestResolver>();
        bool allowInsecureTls = configuration.GetValue("OPENAGENT_ALLOW_INSECURE_TLS", false);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(FileAssetUrlDownloader)))
        {
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
        }
        services.TryAddScoped<FileAssetUrlDownloader>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, FileAssetCapabilitySource>());
        return services;
    }
}
