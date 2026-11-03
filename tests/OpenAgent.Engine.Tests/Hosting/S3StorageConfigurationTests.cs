using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Host.Files;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class S3StorageConfigurationTests
{
    [Theory]
    [InlineData("http://minio:9000", "us-east-1", true)]
    [InlineData("https://s3.eu-west-1.amazonaws.com", "eu-west-1", false)]
    [InlineData("https://objects.example.com", "provider-region", false)]
    public void AddFileAssetObjectStorage_CustomProvider_ConfiguresEndpointAndSigning(
        string endpoint, string region, bool pathStyle)
    {
        IConfiguration configuration = CreateConfiguration(endpoint, region, pathStyle);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileAssetObjectStorage(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        IAmazonS3 client = provider.GetRequiredService<IAmazonS3>();
        var config = Assert.IsType<AmazonS3Config>(client.Config);

        Assert.Equal(endpoint.TrimEnd('/'), config.ServiceURL.TrimEnd('/'));
        Assert.Equal(region, config.AuthenticationRegion);
        Assert.Equal(pathStyle, config.ForcePathStyle);
    }

    [Theory]
    [InlineData("ServiceUrl")]
    [InlineData("AccessKey")]
    [InlineData("SecretKey")]
    public void AddFileAssetObjectStorage_MissingRequiredSetting_FailsBeforeNetwork(string setting)
    {
        IConfiguration configuration = CreateConfiguration("https://objects.example.com", "us-east-1", false);
        configuration[$"FileAssets:ObjectStorage:{setting}"] = "";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileAssetObjectStorage(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException error = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<FileObjectStorageOptions>>().Value);

        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration CreateConfiguration(string endpoint, string region, bool pathStyle) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileAssets:Enabled"] = "true",
            ["FileAssets:ObjectStorage:ServiceUrl"] = endpoint,
            ["FileAssets:ObjectStorage:Region"] = region,
            ["FileAssets:ObjectStorage:ForcePathStyle"] = pathStyle.ToString(),
            ["FileAssets:ObjectStorage:AccessKey"] = "example-access",
            ["FileAssets:ObjectStorage:SecretKey"] = "example-secret"
        }).Build();
}
