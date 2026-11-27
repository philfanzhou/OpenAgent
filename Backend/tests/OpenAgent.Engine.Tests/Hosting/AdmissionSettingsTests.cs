using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenAgent.Application.Execution.Admission;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class AdmissionSettingsTests
{
    [Fact]
    public void ShippedAppSettings_EnableAQueueOfSixteenAndMatchTheCodeDefaults()
    {
        // 随镜像发布的默认配置显式开启 16 个排队名额；与代码默认值保持一致，避免两处漂移。
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "OpenAgent.Engine.Host", "appsettings.json"));
        Assert.True(File.Exists(path), $"shipped appsettings not found: {path}");
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        AdmissionOptions shipped = configuration.GetSection(AdmissionOptions.SectionName).Get<AdmissionOptions>()!;

        Assert.Equal(16, shipped.MaxQueuedTurns);
        Assert.Equal(16, shipped.MaxQueuedPerTenant);
        Assert.Equal(JsonSerializer.Serialize(new AdmissionOptions()), JsonSerializer.Serialize(shipped));
    }
}
