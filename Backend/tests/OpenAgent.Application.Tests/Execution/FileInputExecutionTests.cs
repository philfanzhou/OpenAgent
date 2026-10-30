using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Application.Tests.Runtime;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Execution;

public sealed class FileInputExecutionTests
{
    [Theory]
    [InlineData("input.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("original.png", "image/png")]
    public async Task ExecuteAsync_TextOnlyModelReceivesOriginalAttachmentsThroughWorkspace(string name, string mediaType)
    {
        RecordingFileAssetRepository repository = new();
        RecordingFileObjectStore objects = new();
        IOptions<FileAssetOptions> assets = Options.Create(new FileAssetOptions { Enabled = true });
        FileAssetService files = new(repository, objects, assets);
        byte[] original = [0, 255, 128, 10, 13];
        await using MemoryStream stream = new(original);
        FileAsset asset = await files.UploadAsync(new() { FileName = name, MediaType = mediaType, Source = FileAssetSource.UserUpload },
            stream, new() { TenantId = "tenant-1", UserId = "user-1" }, default);
        FileInputRuntimeDriver driver = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver, configure: services =>
        {
            services.RemoveAll<IFileAssetRepository>();
            services.AddSingleton<IFileAssetRepository>(repository);
            services.AddSingleton<IFileObjectStore>(objects);
            services.AddSingleton(assets);
            services.AddSingleton<IOptions<FileExchangeOptions>>(Options.Create(new FileExchangeOptions { Enabled = true }));
        });
        await runtime.Executor.ExecuteAsync(new AgentRequest
        {
            AgentId = "test-agent", LlmProfileId = "test-model", ConversationId = "files", Query = "Process this file.", FileIds = [asset.FileId]
        }, new AgentUserContext { TenantId = "tenant-1", UserId = "user-1" }, default);
        Assert.Equal(original, driver.Inputs[asset.FileId]);
        Assert.Equal(asset.Sha256, Assert.Single(driver.Preparation!.Files).File.Sha256);
        Assert.Contains("files:" + asset.FileId, repository.References);
    }
}
