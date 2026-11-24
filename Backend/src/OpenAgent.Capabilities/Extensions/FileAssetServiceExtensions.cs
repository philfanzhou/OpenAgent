using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
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
        services.AddOptions<FileExchangeOptions>()
            .Bind(configuration.GetSection(FileExchangeOptions.SectionName))
            .Validate(options => options.MaxInputFiles is > 0 and <= 100
                && options.MaxInputBytes > 0
                && options.MaxOutputFiles is > 0 and <= 100
                && options.MaxOutputBytes > 0
                && options.MaxConcurrentTransfers is > 0 and <= 16, "Invalid file exchange limits.")
            .ValidateOnStart();
        services.TryAddSingleton<FileTransferGate>();
        services.AddScoped<FileExchangeContext>();
        services.AddScoped<ICapabilitySource, FileExchangeCapabilitySource>();
        services.AddSingleton<IValidateOptions<FileShareOptions>, FileShareOptionsValidator>();
        services.AddOptions<FileShareOptions>()
            .Bind(configuration.GetSection(FileShareOptions.SectionName))
            .ValidateOnStart();
        // 本地磁盘 store 作为组合缺省；生产配置对象存储后由 Engine.Host 替换为 S3。
        // Linux 容器里 LocalApplicationData 可能为空，为空时退回临时目录，保证容器可启动。
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string storageRoot = configuration["LocalDevelopment:FileStorageRoot"]
            ?? (string.IsNullOrEmpty(localAppData)
                ? Path.Combine(Path.GetTempPath(), "OpenAgent", "objects")
                : Path.Combine(localAppData, "OpenAgent", "objects"));
        services.TryAddSingleton<IFileObjectStore>(_ => new LocalFileObjectStore(storageRoot));
        // 分享仓储只有 EF 实现；未组合 Framework 的宿主（测试宿主）保持显式报错的缺省，
        // 引擎宿主经 AddOpenAgentFramework 注册真实实现。
        services.TryAddSingleton<IFileShareRepository, UnconfiguredFileShareRepository>();
        services.TryAddSingleton<InlineImageOptimizer>();
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
                // DNS rebinding 防护：地址校验阶段解析的 IP 与 HttpClient 实际连接时重新解析
                // 得到的 IP 可能不同（TOCTOU）。ConnectCallback 在连接级对最终目标 IP 再拦截一次。
                SocketsHttpHandler handler = new()
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    ConnectCallback = async (context, cancellationToken) =>
                    {
                        if (!IPAddress.TryParse(context.DnsEndPoint.Host, out IPAddress? address))
                        {
                            throw new AgentException(
                                AgentErrorCode.InvalidRequest,
                                "只支持不带账号密码的 HTTP(S) 下载地址。");
                        }
                        FileAssetUrlDownloader.EnsureAddressAllowed(address);
                        return await FileAssetUrlDownloader
                            .ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken)
                            .ConfigureAwait(false);
                    },
                };
                if (allowInsecureTls)
                {
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }

                return handler;
            });
        services.AddScoped<FileAssetUrlDownloader>();
        services.AddScoped<ICapabilitySource, FileAssetCapabilitySource>();
        return services;
    }
}
