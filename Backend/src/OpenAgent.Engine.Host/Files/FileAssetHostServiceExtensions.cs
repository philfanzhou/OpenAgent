using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Hosting;

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
        bool allowInsecureTls = HttpClientSecurity.AllowInsecureTls(configuration);
        services.TryAddSingleton<IAmazonS3>(serviceProvider =>
        {
            FileObjectStorageOptions options = serviceProvider
                .GetRequiredService<IOptions<FileObjectStorageOptions>>()
                .Value;
            return CreateS3Client(options, options.ServiceUrl, allowInsecureTls);
        });
        services.TryAddSingleton<IS3Presigner>(serviceProvider =>
        {
            FileObjectStorageOptions options = serviceProvider
                .GetRequiredService<IOptions<FileObjectStorageOptions>>()
                .Value;
            if (string.IsNullOrWhiteSpace(options.PublicServiceUrl))
            {
                return new S3Presigner(serviceProvider.GetRequiredService<IAmazonS3>());
            }

            IAmazonS3 client = CreateS3Client(options, options.PublicServiceUrl, allowInsecureTls);
            return new S3Presigner(client, ownsClient: true);
        });
        services.Replace(ServiceDescriptor.Singleton<IFileObjectStore, S3FileObjectStore>());
        services.AddHealthChecks().AddCheck<FileObjectStorageHealthCheck>(
            "file-object-storage",
            tags: ["infrastructure", "ready"]);
        return services;
    }

    private static IAmazonS3 CreateS3Client(
        FileObjectStorageOptions options,
        string? serviceUrl,
        bool allowInsecureTls)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            HttpClientFactory = new S3HttpClientFactory(allowInsecureTls)
        };
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = serviceUrl;
        }

        return string.IsNullOrWhiteSpace(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    private sealed class S3HttpClientFactory(bool allowInsecureTls) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new HttpClientHandler();
            if (allowInsecureTls)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            return new HttpClient(new EtagStripHandler { InnerHandler = handler });
        }
    }

    private sealed class EtagStripHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Headers.Remove("ETag");
            return response;
        }
    }
}
