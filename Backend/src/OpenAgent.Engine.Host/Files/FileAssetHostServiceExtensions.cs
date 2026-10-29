using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Files;
using OpenAgent.Engine.Host.Health;
using OpenAgent.Engine.Hosting;
using OpenAgent.Framework.ObjectStorage;

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

        // S3 实现与 Options 校验由 Framework/ObjectStorage 提供；宿主只保留开关、
        // TLS 开关解析与健康检查组装，本地磁盘缺省仍由 AddFileAssetServices 注册。
        services.AddS3FileObjectStorage(configuration, HttpClientSecurity.AllowInsecureTls(configuration));
        services.AddHealthChecks().AddCheck<FileObjectStorageHealthCheck>(
            "file-object-storage",
            tags: ["infrastructure", "ready"]);
        return services;
    }
}
