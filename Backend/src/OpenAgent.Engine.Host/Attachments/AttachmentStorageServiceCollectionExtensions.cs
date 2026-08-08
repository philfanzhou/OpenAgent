using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Content;

namespace OpenAgent.Engine.Host.Attachments;

internal static class AttachmentStorageServiceCollectionExtensions
{
    internal static IServiceCollection AddAttachmentStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(
            AttachmentObjectStorageOptions.SectionName);
        services.AddSingleton<IValidateOptions<AttachmentObjectStorageOptions>,
            AttachmentObjectStorageOptionsValidator>();
        services.AddOptions<AttachmentObjectStorageOptions>()
            .Bind(section)
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        if (!section.GetValue<bool>(nameof(AttachmentObjectStorageOptions.Enabled)))
        {
            services.TryAddSingleton<IAttachmentObjectStore, NullAttachmentObjectStore>();
            return services;
        }

        services.TryAddSingleton<IAmazonS3>(serviceProvider =>
        {
            AttachmentObjectStorageOptions options = serviceProvider
                .GetRequiredService<IOptions<AttachmentObjectStorageOptions>>()
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

            if (!string.IsNullOrWhiteSpace(options.AccessKey))
            {
                var credentials = new BasicAWSCredentials(
                    options.AccessKey,
                    options.SecretKey);
                return new AmazonS3Client(credentials, config);
            }

            return new AmazonS3Client(config);
        });
        services.TryAddSingleton<IAttachmentObjectStore, S3AttachmentObjectStore>();
        services.AddHealthChecks()
            .AddCheck<AttachmentStorageHealthCheck>(
                "attachment-object-storage",
                tags: ["infrastructure", "ready"]);
        return services;
    }
}
