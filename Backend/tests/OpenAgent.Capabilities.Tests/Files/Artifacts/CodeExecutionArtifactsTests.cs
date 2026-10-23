using OpenAgent.Capabilities.Tests.TestDoubles;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Capabilities.Files.Artifacts;
using OpenAgent.Capabilities.Files.Services;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Capabilities.Code;

public class CodeExecutionArtifactsTests
{
    [Fact]
    public async Task PublishAsync_MixedOutputs_RegistersAllNonEmptyFiles()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateFiles(repository, objects);
        CodeExecutionResult result = Result(
            new ExecutionFile { Name = "report.html", Content = "<h1>hi</h1>"u8.ToArray() },
            new ExecutionFile { Name = "style.css", Content = "body{}"u8.ToArray() },
            new ExecutionFile { Name = "helper.py", Content = "print(1)"u8.ToArray() },
            new ExecutionFile { Name = "empty.txt", Content = [] });

        CodeExecutionArtifacts.PublishResult publish = await CodeExecutionArtifacts.PublishAsync(
            result, files, Scope(), CancellationToken.None);

        Assert.Equal(3, publish.Files.Count);
        string[] registeredNames = ["helper.py", "report.html", "style.css"];
        Assert.Equal(registeredNames,
            repository.Assets.Values.Select(asset => asset.FileName).OrderBy(name => name).ToArray());
        Assert.Equal("text/html", repository.Assets.Values.Single(a => a.FileName == "report.html").MediaType);
        Assert.Equal("text/css", repository.Assets.Values.Single(a => a.FileName == "style.css").MediaType);
        Assert.Equal("application/octet-stream",
            repository.Assets.Values.Single(a => a.FileName == "helper.py").MediaType);
        CodeExecutionArtifacts.SkippedFile skipped = Assert.Single(publish.Skipped);
        Assert.Equal("empty.txt", skipped.Name);
    }

    [Fact]
    public async Task PublishAsync_StorageRejectsOversizeOutput_SkipsAndReportsReason()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 4,
                MaxFunctionReadBytes = 128
            }));
        CodeExecutionResult result = Result(
            new ExecutionFile { Name = "big.bin", Content = "12345678"u8.ToArray() },
            new ExecutionFile { Name = "notes.txt", Content = "ok"u8.ToArray() });

        CodeExecutionArtifacts.PublishResult publish = await CodeExecutionArtifacts.PublishAsync(
            result, files, Scope(), CancellationToken.None);

        Assert.Single(publish.Files);
        string[] registeredNames = ["notes.txt"];
        Assert.Equal(registeredNames, repository.Assets.Values.Select(asset => asset.FileName).ToArray());
        CodeExecutionArtifacts.SkippedFile skipped = Assert.Single(publish.Skipped);
        Assert.Equal("big.bin", skipped.Name);
        Assert.Contains("size", skipped.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static IFileAssetService CreateFiles(
        RecordingFileAssetRepository repository,
        RecordingFileObjectStore objects) => new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128
            }));

    private static FileAssetScope Scope() => new()
    {
        TenantId = "tenant-a",
        UserId = "user-a",
        ConversationId = "conversation-a"
    };

    private static CodeExecutionResult Result(params ExecutionFile[] files) => new()
    {
        ExecutionId = "exec-a",
        Files = [.. files]
    };
}
