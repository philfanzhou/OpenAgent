using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Engine.Host.Files;

internal static class FileAssetHostServiceExtensions
{
    internal static IServiceCollection AddFileAssetObjectStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        bool enabled = configuration.GetValue<bool>($"{FileAssetOptions.SectionName}:Enabled");
        if (!enabled)
        {
            return services;
        }

        services.AddSingleton<IValidateOptions<FileObjectStorageOptions>, FileObjectStorageOptionsValidator>();
        services.AddOptions<FileObjectStorageOptions>()
            .Bind(configuration.GetSection(FileObjectStorageOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IAmazonS3>(serviceProvider =>
        {
            FileObjectStorageOptions options = serviceProvider
                .GetRequiredService<IOptions<FileObjectStorageOptions>>()
                .Value;
            var config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region
            };
            if (string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
            }
            else
            {
                config.ServiceURL = options.ServiceUrl;
            }

            return string.IsNullOrWhiteSpace(options.AccessKey)
                ? new AmazonS3Client(config)
                : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        });
        services.Replace(ServiceDescriptor.Singleton<IFileObjectStore, S3FileObjectStore>());
        services.AddHealthChecks().AddCheck<FileObjectStorageHealthCheck>(
            "file-object-storage",
            tags: ["infrastructure", "ready"]);
        return services;
    }
}
