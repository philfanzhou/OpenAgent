using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Content;
using OpenAgent.Engine.Host.Attachments;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AttachmentObjectStorageOptionsTests
{
    [Fact]
    public void AddAttachmentStorage_WhenDisabled_UsesNullStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAttachmentStorage(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<NullAttachmentObjectStore>(
            provider.GetRequiredService<IAttachmentObjectStore>());
    }

    [Theory]
    [InlineData("Attachments:ObjectStorage:BucketName", "x")]
    [InlineData("Attachments:ObjectStorage:ServiceUrl", "ftp://object-store")]
    [InlineData("Attachments:ObjectStorage:ServiceUrl", "https://user:secret@object-store")]
    [InlineData("Attachments:ObjectStorage:ServiceUrl", "https://object-store?region=other")]
    [InlineData("Attachments:ObjectStorage:KeyPrefix", "attachments/../shared")]
    [InlineData("Attachments:ObjectStorage:KeyPrefix", "attachments//shared")]
    [InlineData("Attachments:ObjectStorage:AccessKey", "access-only")]
    public void AddAttachmentStorage_WithInvalidEnabledConfiguration_FailsValidation(
        string key,
        string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Attachments:ObjectStorage:Enabled"] = "true",
                [key] = value
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAttachmentStorage(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AttachmentObjectStorageOptions>>().Value);
    }
}
