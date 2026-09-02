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
        services.AddHttpClient("AgentFileDownload", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(FileAssetUrlDownloader.DefaultTimeoutSeconds);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAgent-FileDownloader/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });
        services.AddScoped<FileAssetUrlDownloader>();
        services.AddScoped<ICapabilitySource, FileAssetCapabilitySource>();
        return services;
    }
}
